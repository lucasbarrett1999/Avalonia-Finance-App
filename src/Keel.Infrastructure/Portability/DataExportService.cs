using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Keel.Application.Files;
using Keel.Application.Portability;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Portability;

/// <summary>
/// The full export (F-REP-6, ADR 0098): CSV files and the JSON bundle, each written from one read snapshot of
/// the open file, transactions in pages of <see cref="PageSize"/> and every other table streamed row by row.
/// Files are written under a temporary name and moved into place when complete.
/// </summary>
public sealed partial class DataExportService(
    IDbContextFactory<KeelDbContext> factory,
    IBudgetFileService files,
    IDataDirectory dataDirectory,
    TimeProvider time,
    ILogger<DataExportService> logger) : IDataExportService
{
    /// <summary>Transactions per page of the CSV export.</summary>
    public const int PageSize = 2_000;

    private const int FlushEvery = 1_000;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The version written into bundles (the assembly's informational version without build metadata).</summary>
    public static string AppVersion { get; } = (typeof(DataExportService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <inheritdoc />
    public Task<CsvExportResult> ExportCsvAsync(string destination, bool asZip, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return Task.Run(
            async () =>
            {
                var target = Path.GetFullPath(destination);
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    var snapshot = await ReadSnapshot.BeginAsync(db, ct).ConfigureAwait(false);
                    await using (snapshot.ConfigureAwait(false))
                    {
                        var lookups = await Lookups.LoadAsync(db, ct).ConfigureAwait(false);
                        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
                        if (asZip)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            var partial = target + ".partial";
                            try
                            {
                                using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
                                {
                                    foreach (var name in CsvExportFiles.All)
                                    {
                                        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                                        var stream = entry.Open();
                                        await using (stream.ConfigureAwait(false))
                                        {
                                            rows[name] = await WriteCsvAsync(db, lookups, name, stream, ct).ConfigureAwait(false);
                                        }
                                    }
                                }

                                File.Move(partial, target, overwrite: true);
                            }
                            finally
                            {
                                File.Delete(partial);
                            }
                        }
                        else
                        {
                            Directory.CreateDirectory(target);
                            if (CsvExportFiles.All.FirstOrDefault(n => File.Exists(Path.Combine(target, n))) is { } existing)
                            {
                                throw new IOException($"The folder already holds an export ({existing}). Choose an empty folder.");
                            }

                            foreach (var name in CsvExportFiles.All)
                            {
                                var path = Path.Combine(target, name);
                                var stream = new FileStream(path + ".partial", FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                                await using (stream.ConfigureAwait(false))
                                {
                                    rows[name] = await WriteCsvAsync(db, lookups, name, stream, ct).ConfigureAwait(false);
                                }

                                File.Move(path + ".partial", path);
                            }
                        }

                        LogCsvExported(logger, rows.GetValueOrDefault(CsvExportFiles.Transactions), asZip);
                        return new CsvExportResult(target, rows);
                    }
                }
            },
            ct);
    }

    /// <inheritdoc />
    public Task<BundleInfo> ExportBundleAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(
            async () =>
            {
                var target = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    var snapshot = await ReadSnapshot.BeginAsync(db, ct).ConfigureAwait(false);
                    await using (snapshot.ConfigureAwait(false))
                    {
                        var tables = BundleSchema.Tables(db.Model);
                        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
                        foreach (var table in tables)
                        {
                            counts[table.Name] = await BundleSchema.CountAsync(db, table, ct).ConfigureAwait(false);
                        }

                        var schema = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).LastOrDefault();
                        var attachments = AttachmentFiles();
                        var sourceFile = Path.GetFileName(files.CurrentPath ?? string.Empty);
                        var exportedAt = time.GetUtcNow().UtcDateTime;
                        var partial = target + ".partial";
                        try
                        {
                            var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                            await using (stream.ConfigureAwait(false))
                            {
                                var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                                await using (writer.ConfigureAwait(false))
                                {
                                    writer.WriteStartObject();
                                    writer.WriteString("format", BundleFormat.Name);
                                    writer.WriteNumber("version", BundleFormat.Version);
                                    writer.WriteString("exportedAt", exportedAt.ToString("O", CultureInfo.InvariantCulture));
                                    writer.WriteString("app", AppVersion);
                                    writer.WriteString("sourceFile", sourceFile);
                                    writer.WriteString("schema", schema);
                                    writer.WriteStartObject("counts");
                                    foreach (var (name, count) in counts)
                                    {
                                        writer.WriteNumber(name, count);
                                    }

                                    writer.WriteEndObject();
                                    writer.WriteNumber("attachmentFiles", attachments.Count);
                                    writer.WriteStartArray("tables");
                                    foreach (var table in tables)
                                    {
                                        await WriteTableAsync(writer, db, table, counts[table.Name], ct).ConfigureAwait(false);
                                    }

                                    writer.WriteEndArray();
                                    writer.WriteStartArray("attachments");
                                    foreach (var (relative, full) in attachments)
                                    {
                                        writer.WriteStartObject();
                                        writer.WriteString("path", relative);
                                        writer.WriteBase64String("data", await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false));
                                        writer.WriteEndObject();
                                        await writer.FlushAsync(ct).ConfigureAwait(false);
                                    }

                                    writer.WriteEndArray();
                                    writer.WriteEndObject();
                                    await writer.FlushAsync(ct).ConfigureAwait(false);
                                }
                            }

                            File.Move(partial, target, overwrite: true);
                        }
                        finally
                        {
                            File.Delete(partial);
                        }

                        LogBundleExported(logger, counts.GetValueOrDefault(nameof(Transaction)), attachments.Count);
                        return new BundleInfo(target, BundleFormat.Version, exportedAt, AppVersion, sourceFile, schema, counts, attachments.Count);
                    }
                }
            },
            ct);
    }

    private static async Task WriteTableAsync(Utf8JsonWriter writer, KeelDbContext db, BundleTable table, long expected, CancellationToken ct)
    {
        writer.WriteStartObject();
        writer.WriteString("entity", table.Name);
        writer.WriteString("table", table.TableName);
        writer.WriteStartArray("columns");
        foreach (var property in table.Properties)
        {
            writer.WriteStringValue(property.Name);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("rows");
        var getters = table.Properties.Select(p => p.GetGetter()).ToList();
        long written = 0;
        await foreach (var row in BundleSchema.Rows(db, table).WithCancellation(ct).ConfigureAwait(false))
        {
            writer.WriteStartArray();
            foreach (var getter in getters)
            {
                BundleSchema.WriteValue(writer, getter.GetClrValue(row));
            }

            writer.WriteEndArray();
            if (++written % FlushEvery == 0)
            {
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        if (written != expected)
        {
            throw new InvalidOperationException($"The {table.Name} table changed while it was exported.");
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    // The attachments folder next to the open file: every file with its path relative to the folder.
    private List<(string Relative, string Full)> AttachmentFiles()
    {
        if (files.CurrentPath is not { } current)
        {
            return [];
        }

        var folder = dataDirectory.AttachmentsDirectoryFor(current);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => (Path.GetRelativePath(folder, f).Replace(Path.DirectorySeparatorChar, '/'), f))
                .OrderBy(f => f.Item1, StringComparer.Ordinal)
                .ToList()
            : [];
    }

    private static async Task<int> WriteCsvAsync(KeelDbContext db, Lookups lookups, string name, Stream stream, CancellationToken ct)
    {
        var writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 64 * 1024, leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { NewLine = "\n" });
            await using (csv.ConfigureAwait(false))
            {
                var rows = name switch
                {
                    CsvExportFiles.Transactions => await CsvTables.TransactionsAsync(csv, db, lookups, PageSize, ct).ConfigureAwait(false),
                    CsvExportFiles.Budget => await CsvTables.BudgetAsync(csv, db, lookups, ct).ConfigureAwait(false),
                    CsvExportFiles.Accounts => await CsvTables.AccountsAsync(csv, db, ct).ConfigureAwait(false),
                    CsvExportFiles.Categories => await CsvTables.CategoriesAsync(csv, db, lookups, ct).ConfigureAwait(false),
                    CsvExportFiles.Payees => await CsvTables.PayeesAsync(csv, db, lookups, ct).ConfigureAwait(false),
                    CsvExportFiles.Rules => await CsvTables.RulesAsync(csv, db, ct).ConfigureAwait(false),
                    CsvExportFiles.Targets => await CsvTables.TargetsAsync(csv, db, lookups, ct).ConfigureAwait(false),
                    CsvExportFiles.Scheduled => await CsvTables.ScheduledAsync(csv, db, lookups, ct).ConfigureAwait(false),
                    CsvExportFiles.Recurring => await CsvTables.RecurringAsync(csv, db, lookups, ct).ConfigureAwait(false),
                    _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
                };
                await csv.FlushAsync().ConfigureAwait(false);
                return rows;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "CSV export written: {Transactions} transaction lines (zip: {Zip})")]
    private static partial void LogCsvExported(ILogger logger, int transactions, bool zip);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bundle export written: {Transactions} transactions, {Attachments} attachment files")]
    private static partial void LogBundleExported(ILogger logger, long transactions, int attachments);
}
