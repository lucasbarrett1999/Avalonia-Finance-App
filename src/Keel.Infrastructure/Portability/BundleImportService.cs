using System.Globalization;
using System.Text.Json;
using Keel.Application.Files;
using Keel.Application.Portability;
using Keel.Domain.Entities;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Portability;

/// <summary>
/// Imports a JSON bundle (F-REP-6, ADR 0098) into a new or empty budget file: the bundle is streamed table by
/// table and written by <see cref="BulkTableWriter"/> in one transaction with foreign keys deferred; before the
/// commit every foreign key (<c>PRAGMA foreign_key_check</c>) and the split-sum invariant are validated, and
/// the row counts must match the header. Nothing of a failed import remains: the transaction rolls back and a
/// file created by the import is deleted.
/// </summary>
public sealed partial class BundleImportService(IDataDirectory dataDirectory, TimeProvider time, ILogger<BundleImportService> logger) : IBundleImportService
{
    /// <inheritdoc />
    public Task<BundleInfo> ReadInfoAsync(string bundlePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        return Task.Run(
            () =>
            {
                using var stream = OpenBundle(bundlePath);
                return Guard(() => ReadHeader(new JsonTokenStream(stream), Path.GetFullPath(bundlePath)));
            },
            ct);
    }

    /// <inheritdoc />
    public Task<BundleImportResult> ImportIntoNewFileAsync(string bundlePath, string targetPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        return Task.Run(
            async () =>
            {
                var target = Path.GetFullPath(targetPath);
                var created = !File.Exists(target);
                var staging = Path.Combine(Path.GetDirectoryName(target)!, ".keel-import-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var (rows, files) = await ImportCoreAsync(Path.GetFullPath(bundlePath), target, created, staging, ct).ConfigureAwait(false);
                    if (files > 0)
                    {
                        MoveAttachments(staging, dataDirectory.AttachmentsDirectoryFor(target));
                    }

                    LogImported(logger, rows.GetValueOrDefault(nameof(Transaction)), files);
                    return new BundleImportResult(target, rows, files);
                }
                catch (Exception ex) when (created && ex is not OutOfMemoryException)
                {
                    DeleteBudgetFile(target);
                    throw;
                }
                finally
                {
                    BackupArchive.TryDeleteDirectory(staging);
                }
            },
            ct);
    }

    private async Task<(Dictionary<string, long> Rows, int Files)> ImportCoreAsync(string bundlePath, string target, bool created, string staging, CancellationToken ct)
    {
        using var stream = OpenBundle(bundlePath);
        var json = new JsonTokenStream(stream);
        var header = Guard(() => ReadHeader(json, bundlePath));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var db = KeelDbContextFactory.CreateForFile(target);
        await using (db.ConfigureAwait(false))
        {
            await PrepareTargetAsync(db, created, header, ct).ConfigureAwait(false);
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON;", ct).ConfigureAwait(false);
                    var tables = BundleSchema.Tables(db.Model).ToDictionary(t => t.Name, StringComparer.Ordinal);
                    var read = new Dictionary<string, long>(StringComparer.Ordinal);
                    var written = new Dictionary<string, long>(StringComparer.Ordinal);
                    var files = 0;
                    var now = time.GetUtcNow().UtcDateTime;
                    await GuardAsync(async () =>
                    {
                        json.Expect(JsonTokenType.StartArray);
                        while (json.Read() && json.TokenType == JsonTokenType.StartObject)
                        {
                            await ReadTableAsync(json, db, tables, read, written, now, ct).ConfigureAwait(false);
                        }

                        while (json.Read() && json.TokenType == JsonTokenType.PropertyName)
                        {
                            if (json.Text == "attachments")
                            {
                                files = ReadAttachments(json, staging);
                            }
                            else
                            {
                                json.Read();
                                json.SkipValue();
                            }
                        }
                    }).ConfigureAwait(false);

                    foreach (var table in tables.Values)
                    {
                        if (read.GetValueOrDefault(table.Name) != header.Count(table.Name))
                        {
                            throw new BundleException(BundleError.NotABundle, $"The bundle is incomplete: the {table.Name} table does not have the rows its header lists.");
                        }
                    }

                    if (files != header.AttachmentFiles)
                    {
                        throw new BundleException(BundleError.NotABundle, "The bundle is incomplete: attachment files are missing.");
                    }

                    await ValidateAsync(db, ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return (written, files);
                }
            }
            finally
            {
                var connection = (SqliteConnection)db.Database.GetDbConnection();
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
                SqliteConnection.ClearPool(connection);
            }
        }
    }

    // A new file gets the schema; an existing one must be a Keel file of a known schema with nothing in it.
    private static async Task PrepareTargetAsync(KeelDbContext db, bool created, BundleInfo header, CancellationToken ct)
    {
        var known = db.Database.GetMigrations().ToList();
        if (header.Schema is { } schema && !known.Contains(schema, StringComparer.Ordinal) && string.CompareOrdinal(schema, known.LastOrDefault()) > 0)
        {
            throw new BundleException(BundleError.TooNew, "The bundle was exported by a newer version of Keel. Update Keel to import it.");
        }

        if (!created)
        {
            var applied = await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false);
            if (applied.Any(m => !known.Contains(m, StringComparer.Ordinal)))
            {
                throw new BundleException(BundleError.TargetNotEmpty, "The target budget file was created by a newer version of Keel.");
            }
        }

        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        if (!created && !await IsEmptyAsync(db, ct).ConfigureAwait(false))
        {
            throw new BundleException(BundleError.TargetNotEmpty, "The target budget file already holds data; a bundle is imported only into a new, empty file.");
        }
    }

    private static async Task<bool> IsEmptyAsync(KeelDbContext db, CancellationToken ct) =>
        !await db.Accounts.AnyAsync(ct).ConfigureAwait(false)
        && !await db.Transactions.IgnoreQueryFilters().AnyAsync(ct).ConfigureAwait(false)
        && !await db.CategoryGroups.AnyAsync(g => !g.IsSystem, ct).ConfigureAwait(false)
        && !await db.Categories.AnyAsync(c => !c.IsSystem, ct).ConfigureAwait(false)
        && !await db.Payees.AnyAsync(ct).ConfigureAwait(false)
        && !await db.Rules.AnyAsync(ct).ConfigureAwait(false)
        && !await db.Tags.AnyAsync(ct).ConfigureAwait(false)
        && !await db.BudgetAssignments.AnyAsync(ct).ConfigureAwait(false)
        && !await db.ScheduledTransactions.AnyAsync(ct).ConfigureAwait(false);

    private static async Task ReadTableAsync(
        JsonTokenStream json,
        KeelDbContext db,
        IReadOnlyDictionary<string, BundleTable> tables,
        Dictionary<string, long> read,
        Dictionary<string, long> written,
        DateTime now,
        CancellationToken ct)
    {
        BundleTable? table = null;
        int[]? columnIndexes = null;
        while (json.Read() && json.TokenType == JsonTokenType.PropertyName)
        {
            switch (json.Text)
            {
                case "entity":
                    json.Expect(JsonTokenType.String);
                    if (!tables.TryGetValue(json.Text!, out table))
                    {
                        throw new BundleException(BundleError.TooNew, $"The bundle holds a table this version of Keel does not know ({json.Text}). Update Keel to import it.");
                    }

                    break;
                case "columns":
                    if (table is null)
                    {
                        throw new JsonException("A table lists its columns before its entity.");
                    }

                    json.Expect(JsonTokenType.StartArray);
                    var names = new List<string>();
                    while (json.Read() && json.TokenType == JsonTokenType.String)
                    {
                        names.Add(json.Text!);
                    }

                    columnIndexes = names.Select(n =>
                    {
                        var index = table.Properties.ToList().FindIndex(p => p.Name == n);
                        return index >= 0 ? index : throw new BundleException(BundleError.TooNew, $"The bundle holds data this version of Keel does not know ({table.Name}.{n}). Update Keel to import it.");
                    }).ToArray();
                    break;
                case "rows":
                    if (table is null || columnIndexes is null)
                    {
                        throw new JsonException("A table lists its rows before its entity and columns.");
                    }

                    json.Expect(JsonTokenType.StartArray);
                    var (rows, stored) = await ReadRowsAsync(json, db, table, columnIndexes, now, ct).ConfigureAwait(false);
                    read[table.Name] = read.GetValueOrDefault(table.Name) + rows;
                    written[table.Name] = written.GetValueOrDefault(table.Name) + stored;
                    break;
                default:
                    json.Read();
                    json.SkipValue();
                    break;
            }
        }
    }

    private static async Task<(long Read, long Written)> ReadRowsAsync(JsonTokenStream json, KeelDbContext db, BundleTable table, int[] columns, DateTime now, CancellationToken ct)
    {
        // Columns missing from the bundle keep the entity's own defaults (a column added after the export).
        var template = Activator.CreateInstance(table.EntityType.ClrType)!;
        var defaults = table.Properties.Select(p => p.GetGetter().GetClrValue(template)).ToArray();
        long read = 0, written = 0;
        var writer = BulkTableWriter.For(db, table, now);
        await using (writer.ConfigureAwait(false))
        {
            while (json.Read() && json.TokenType == JsonTokenType.StartArray)
            {
                var values = (object?[])defaults.Clone();
                foreach (var index in columns)
                {
                    var property = table.Properties[index];
                    if (!json.Read() || !BundleSchema.TryReadValue(json, property.ClrType, out var value))
                    {
                        throw new BundleException(BundleError.Invalid, $"A value in {table.Name}.{property.Name} cannot be read.");
                    }

                    values[index] = value;
                }

                json.Expect(JsonTokenType.EndArray);
                read++;
                if (table.EntityType.ClrType == typeof(Setting) && BundleSchema.ExcludedSettingKeys.Contains((string)values[table.Properties.ToList().FindIndex(p => p.Name == nameof(Setting.Key))]!))
                {
                    continue;
                }

                await writer.WriteAsync(values, ct).ConfigureAwait(false);
                written++;
            }
        }

        return (read, written);
    }

    // Attachment files go to a staging folder first; they join the file's attachments folder after the commit.
    private static int ReadAttachments(JsonTokenStream json, string staging)
    {
        json.Expect(JsonTokenType.StartArray);
        var root = Path.GetFullPath(staging);
        var count = 0;
        while (json.Read() && json.TokenType == JsonTokenType.StartObject)
        {
            string? relative = null;
            byte[]? data = null;
            while (json.Read() && json.TokenType == JsonTokenType.PropertyName)
            {
                var name = json.Text;
                json.Read();
                switch (name)
                {
                    case "path" when json.TokenType == JsonTokenType.String:
                        relative = json.Text;
                        break;
                    case "data" when json.TokenType == JsonTokenType.String:
                        data = Convert.FromBase64String(json.Text!);
                        break;
                    default:
                        json.SkipValue();
                        break;
                }
            }

            if (string.IsNullOrEmpty(relative) || data is null)
            {
                throw new BundleException(BundleError.NotABundle, "An attachment of the bundle is damaged.");
            }

            var destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new BundleException(BundleError.Invalid, "An attachment of the bundle points outside the attachments folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, data);
            count++;
        }

        return count;
    }

    // PRD 6.2 invariants, checked before the commit so the error names the problem: splits add up to their
    // transaction (the triggers' violation table is empty) and every foreign key points at a row.
    private static async Task ValidateAsync(KeelDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """SELECT COUNT(*) FROM "SplitSumViolations";""";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
            {
                throw new BundleException(BundleError.Invalid, "The bundle holds split transactions whose lines do not add up to the transaction.");
            }

            command.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var table = reader.GetString(0);
                var parent = reader.GetString(2);
                throw new BundleException(BundleError.Invalid, $"The bundle holds a {table} row that refers to a missing {parent} row.");
            }
        }
    }

    private static void MoveAttachments(string staging, string attachments)
    {
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(attachments, Path.GetRelativePath(staging, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
            {
                File.Move(file, destination);
            }
        }
    }

    private static void DeleteBudgetFile(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the failed import's error is what the user needs to see.
            }
        }
    }

    private static FileStream OpenBundle(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new BundleException(BundleError.NotABundle, "The bundle file does not exist.", ex);
        }
    }

    // The header: "format" first, then version and the export's facts, up to the "tables" property.
    private static BundleInfo ReadHeader(JsonTokenStream json, string path)
    {
        json.Expect(JsonTokenType.StartObject);
        if (!json.Read() || json.TokenType != JsonTokenType.PropertyName || json.Text != "format" || !json.Read() || json.Text != BundleFormat.Name)
        {
            throw new BundleException(BundleError.NotABundle, "The file is not a Keel export bundle.");
        }

        int? version = null;
        DateTime exportedAt = default;
        string app = string.Empty, sourceFile = string.Empty;
        string? schema = null;
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var attachments = 0;
        while (json.Read() && json.TokenType == JsonTokenType.PropertyName)
        {
            var name = json.Text;
            if (name == "tables")
            {
                if (version is null)
                {
                    break;
                }

                return new BundleInfo(path, version.Value, exportedAt, app, sourceFile, schema, counts, attachments);
            }

            json.Read();
            switch (name)
            {
                case "version" when json.TokenType == JsonTokenType.Number:
                    version = int.Parse(json.Text!, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    if (version > BundleFormat.Version)
                    {
                        throw new BundleException(BundleError.TooNew, $"The bundle uses export format {version}, which is newer than this version of Keel reads ({BundleFormat.Version}). Update Keel to import it.");
                    }

                    if (version < 1)
                    {
                        throw new BundleException(BundleError.NotABundle, "The bundle's format version is not valid.");
                    }

                    break;
                case "exportedAt" when json.TokenType == JsonTokenType.String:
                    exportedAt = DateTime.Parse(json.Text!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                    break;
                case "app" when json.TokenType == JsonTokenType.String:
                    app = json.Text!;
                    break;
                case "sourceFile" when json.TokenType == JsonTokenType.String:
                    sourceFile = json.Text!;
                    break;
                case "schema":
                    schema = json.TokenType == JsonTokenType.String ? json.Text : null;
                    break;
                case "counts" when json.TokenType == JsonTokenType.StartObject:
                    while (json.Read() && json.TokenType == JsonTokenType.PropertyName)
                    {
                        var table = json.Text!;
                        json.Expect(JsonTokenType.Number);
                        counts[table] = long.Parse(json.Text!, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    }

                    break;
                case "attachmentFiles" when json.TokenType == JsonTokenType.Number:
                    attachments = int.Parse(json.Text!, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    break;
                default:
                    json.SkipValue();
                    break;
            }
        }

        throw new BundleException(BundleError.NotABundle, "The file is not a Keel export bundle (its header is incomplete).");
    }

    private static T Guard<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or IOException or InvalidOperationException)
        {
            throw new BundleException(BundleError.NotABundle, "The file is not a Keel export bundle, or it is damaged: " + ex.Message, ex);
        }
    }

    private static async Task GuardAsync(Func<Task> read)
    {
        try
        {
            await read().ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new BundleException(BundleError.NotABundle, "The bundle is damaged: " + ex.Message, ex);
        }
        catch (FormatException ex)
        {
            throw new BundleException(BundleError.NotABundle, "The bundle is damaged: " + ex.Message, ex);
        }
        catch (SqliteException ex)
        {
            throw new BundleException(BundleError.Invalid, "The bundle's rows break the budget file's rules: " + ex.Message, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Bundle imported into a new budget file: {Transactions} transactions, {Files} attachment files")]
    private static partial void LogImported(ILogger logger, long transactions, int files);
}
