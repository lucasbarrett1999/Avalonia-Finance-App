using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Keel.Application.Files;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Recurring;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Files;

/// <summary>
/// Integrity check, consistent copies, deletion of moved files, and the diagnostic bundle for the open
/// budget file (F-SET-1, PRD 10). The last integrity-check day is kept in the file's Setting table so it
/// travels with the file.
/// </summary>
public sealed partial class DataFileMaintenance(
    KeelDbContextFactory factory,
    IDataDirectory dataDirectory,
    TimeProvider time,
    ILogger<DataFileMaintenance> logger) : IDataFileMaintenance
{
    /// <summary>Setting key of the last integrity-check day (yyyy-MM-dd).</summary>
    public const string IntegrityCheckedOnKey = "maintenance.integrityCheckedOn";

    private static readonly JsonSerializerOptions SummaryJson = new() { WriteIndented = true };

    /// <inheritdoc />
    public Task<IntegrityCheckResult> CheckIntegrityAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            var path = CurrentFile();
            IReadOnlyList<string> problems;
            try
            {
                using var connection = new SqliteConnection(BackupArchive.UnpooledConnectionString(path, SqliteOpenMode.ReadWrite));
                await connection.OpenAsync(ct).ConfigureAwait(false);
                BackupArchive.SetBusyTimeout(connection);
                problems = BackupArchive.IntegrityCheck(connection);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
            {
                // SQLITE_CORRUPT / SQLITE_NOTADB: damage bad enough that the check itself cannot run.
                problems = [ex.Message];
            }

            if (problems.Count > 0)
            {
                LogIntegrityFailed(logger, problems.Count);
            }
            else
            {
                var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
                await DataFileSettings.SetAsync(factory, IntegrityCheckedOnKey, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            }

            return new IntegrityCheckResult(problems.Count == 0, problems, time.GetUtcNow().UtcDateTime);
        },
        ct);

    /// <inheritdoc />
    public async Task<IntegrityCheckResult?> CheckIntegrityIfDueAsync(DateOnly today, CancellationToken ct)
    {
        var db = factory.CreateDbContext();
        string last;
        await using (db.ConfigureAwait(false))
        {
            last = await DataFileSettings.GetAsync(db, IntegrityCheckedOnKey, string.Empty, ct).ConfigureAwait(false);
        }

        if (last == today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        {
            return null;
        }

        return await CheckIntegrityAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task CopyToAsync(string destinationPath, CancellationToken ct) => Task.Run(
        () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
            var source = CurrentFile();
            var destination = Path.GetFullPath(destinationPath);
            if (string.Equals(source, destination, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The budget file is already there.");
            }

            var attachmentsTarget = dataDirectory.AttachmentsDirectoryFor(destination);
            if (File.Exists(destination) || Directory.Exists(attachmentsTarget))
            {
                throw new IOException($"'{Path.GetFileName(destination)}' already exists in that folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                BackupArchive.CopyDatabase(source, destination);
                BackupArchive.CopyDirectory(dataDirectory.AttachmentsDirectoryFor(source), attachmentsTarget);
                BackupArchive.VerifyDatabase(destination);
            }
            catch
            {
                DeleteFiles(destination);
                throw;
            }

            LogCopied(logger);
        },
        ct);

    /// <inheritdoc />
    public Task DeleteBudgetFileAsync(string path, CancellationToken ct) => Task.Run(
        () =>
        {
            var full = Path.GetFullPath(path);
            if (string.Equals(full, factory.CurrentPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The open budget file cannot be deleted.");
            }

            SqliteConnection.ClearAllPools();
            DeleteFiles(full);
        },
        ct);

    /// <inheritdoc />
    public Task<string> CreateDiagnosticBundleAsync(string destinationDirectory, string appVersion, CancellationToken ct) => Task.Run(
        () =>
        {
            Directory.CreateDirectory(destinationDirectory);
            var stamp = time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var zipPath = Path.Combine(destinationDirectory, $"keel-diagnostics-{stamp}.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var summary = zip.CreateEntry("schema-summary.json");
                using (var stream = summary.Open())
                {
                    JsonSerializer.Serialize(stream, BuildSummary(appVersion), SummaryJson);
                }

                if (Directory.Exists(dataDirectory.LogsDirectory))
                {
                    foreach (var log in Directory.EnumerateFiles(dataDirectory.LogsDirectory, "*", SearchOption.TopDirectoryOnly))
                    {
                        // Serilog keeps today's file open for writing; share it instead of failing.
                        using var input = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var entry = zip.CreateEntry("logs/" + Path.GetFileName(log));
                        using var output = entry.Open();
                        input.CopyTo(output);
                    }
                }
            }

            LogBundle(logger);
            return zipPath;
        },
        ct);

    /// <summary>
    /// The schema-only summary: SQLite settings, migrations, and for each table its columns, indexes and
    /// row count. No row values are read, so no payees, amounts, notes or names leave the file.
    /// </summary>
    public DiagnosticSummary BuildSummary(string appVersion)
    {
        var path = factory.CurrentPath;
        var tables = new List<DiagnosticTable>();
        var migrations = new List<string>();
        var pragmas = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long fileSize = 0;
        if (path is not null && File.Exists(path))
        {
            fileSize = new FileInfo(path).Length;
            using var connection = new SqliteConnection(BackupArchive.UnpooledConnectionString(path, SqliteOpenMode.ReadWrite));
            connection.Open();
            BackupArchive.SetBusyTimeout(connection);
            foreach (var pragma in new[] { "page_size", "page_count", "freelist_count", "journal_mode", "user_version", "encoding", "foreign_keys" })
            {
                pragmas[pragma] = Scalar(connection, $"PRAGMA {pragma};")?.ToString() ?? string.Empty;
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";""";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    migrations.Add(reader.GetString(0));
                }
            }

            var names = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    names.Add(reader.GetString(0));
                }
            }

            foreach (var table in names)
            {
                var quoted = "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                var columns = new List<string>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"PRAGMA table_info({quoted});";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        columns.Add($"{reader.GetString(1)} {reader.GetString(2)}".Trim());
                    }
                }

                var indexes = new List<string>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"PRAGMA index_list({quoted});";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        indexes.Add(reader.GetString(1));
                    }
                }

                var rows = Convert.ToInt64(Scalar(connection, $"SELECT COUNT(*) FROM {quoted};"), CultureInfo.InvariantCulture);
                tables.Add(new DiagnosticTable(table, rows, columns, indexes));
            }
        }

        return new DiagnosticSummary(
            appVersion,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            time.GetUtcNow().UtcDateTime,
            path is null ? null : Path.GetFileName(path),
            fileSize,
            pragmas,
            migrations,
            tables);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private void DeleteFiles(string budgetFile)
    {
        foreach (var file in new[] { budgetFile, budgetFile + "-wal", budgetFile + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        var attachments = dataDirectory.AttachmentsDirectoryFor(budgetFile);
        if (Directory.Exists(attachments))
        {
            Directory.Delete(attachments, recursive: true);
        }
    }

    private string CurrentFile() => factory.CurrentPath ?? throw new InvalidOperationException("No budget file is open.");

    [LoggerMessage(Level = LogLevel.Error, Message = "The budget file failed its integrity check ({Count} problem(s))")]
    private static partial void LogIntegrityFailed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Budget file copied to a new location")]
    private static partial void LogCopied(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Diagnostic bundle written")]
    private static partial void LogBundle(ILogger logger);
}

/// <summary>Schema-only summary written into the diagnostic bundle.</summary>
/// <param name="AppVersion">Keel version.</param>
/// <param name="OperatingSystem">OS description.</param>
/// <param name="Architecture">OS architecture.</param>
/// <param name="Runtime">.NET runtime.</param>
/// <param name="CreatedAt">UTC time stamp.</param>
/// <param name="BudgetFileName">File name only (no folder).</param>
/// <param name="BudgetFileSize">Size in bytes.</param>
/// <param name="Pragmas">SQLite settings.</param>
/// <param name="Migrations">Applied migrations.</param>
/// <param name="Tables">Tables with columns, indexes and row counts.</param>
public sealed record DiagnosticSummary(
    string AppVersion,
    string OperatingSystem,
    string Architecture,
    string Runtime,
    DateTime CreatedAt,
    string? BudgetFileName,
    long BudgetFileSize,
    IReadOnlyDictionary<string, string> Pragmas,
    IReadOnlyList<string> Migrations,
    IReadOnlyList<DiagnosticTable> Tables);

/// <summary>A table in the diagnostic summary.</summary>
/// <param name="Name">Table name.</param>
/// <param name="RowCount">Number of rows (a count only).</param>
/// <param name="Columns">"Name TYPE" per column.</param>
/// <param name="Indexes">Index names.</param>
public sealed record DiagnosticTable(string Name, long RowCount, IReadOnlyList<string> Columns, IReadOnlyList<string> Indexes);
