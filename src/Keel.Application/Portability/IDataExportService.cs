namespace Keel.Application.Portability;

/// <summary>
/// Full export of the open budget file (F-REP-6): a set of CSV files for spreadsheets and other tools, and
/// a versioned JSON bundle that re-imports losslessly into a new budget file (<see cref="IBundleImportService"/>).
/// Both read one consistent snapshot of the file page by page and never hold every row in memory.
/// </summary>
public interface IDataExportService
{
    /// <summary>
    /// Writes the CSV files (<see cref="CsvExportFiles"/>) into the folder <paramref name="destination"/>
    /// (created if missing; must not already hold an export), or into a zip at <paramref name="destination"/>
    /// when <paramref name="asZip"/> is true.
    /// </summary>
    Task<CsvExportResult> ExportCsvAsync(string destination, bool asZip, CancellationToken ct);

    /// <summary>Writes the JSON bundle (format <see cref="BundleFormat.Name"/> version <see cref="BundleFormat.Version"/>) to <paramref name="path"/>.</summary>
    Task<BundleInfo> ExportBundleAsync(string path, CancellationToken ct);
}

/// <summary>File names of the CSV export, in the order they are written.</summary>
public static class CsvExportFiles
{
    /// <summary>Transactions, one line per transaction or split line.</summary>
    public const string Transactions = "transactions.csv";

    /// <summary>Assigned amounts per category and month.</summary>
    public const string Budget = "budget.csv";

    /// <summary>Accounts with their balances.</summary>
    public const string Accounts = "accounts.csv";

    /// <summary>Category groups and categories.</summary>
    public const string Categories = "categories.csv";

    /// <summary>Payees.</summary>
    public const string Payees = "payees.csv";

    /// <summary>Rules with their conditions and actions as JSON.</summary>
    public const string Rules = "rules.csv";

    /// <summary>Category targets.</summary>
    public const string Targets = "targets.csv";

    /// <summary>Scheduled transactions.</summary>
    public const string Scheduled = "scheduled-transactions.csv";

    /// <summary>Recurring items (bills and subscriptions).</summary>
    public const string Recurring = "recurring-items.csv";

    /// <summary>Every file, in order.</summary>
    public static IReadOnlyList<string> All { get; } = [Transactions, Budget, Accounts, Categories, Payees, Rules, Targets, Scheduled, Recurring];
}

/// <summary>What a CSV export wrote.</summary>
/// <param name="Path">The folder or zip written.</param>
/// <param name="Rows">Data rows per file name (header lines not counted).</param>
public sealed record CsvExportResult(string Path, IReadOnlyDictionary<string, int> Rows);

/// <summary>The JSON bundle format (F-REP-6, ADR 0098).</summary>
public static class BundleFormat
{
    /// <summary>The <c>format</c> value of every bundle.</summary>
    public const string Name = "keel-export";

    /// <summary>The bundle version this build writes and the newest it reads.</summary>
    public const int Version = 1;

    /// <summary>File extension suggested for bundles.</summary>
    public const string Extension = ".json";
}

/// <summary>A bundle's header: who wrote it and how many rows each table holds.</summary>
/// <param name="Path">Bundle file.</param>
/// <param name="Version">Format version.</param>
/// <param name="ExportedAt">UTC time of the export.</param>
/// <param name="AppVersion">Keel version that wrote it.</param>
/// <param name="SourceFile">File name of the exported budget file.</param>
/// <param name="Schema">Newest database migration of the exported file.</param>
/// <param name="Counts">Rows per table (by EF entity name, e.g. <c>Transaction</c>).</param>
/// <param name="AttachmentFiles">Attachment files carried in the bundle.</param>
public sealed record BundleInfo(
    string Path,
    int Version,
    DateTime ExportedAt,
    string AppVersion,
    string SourceFile,
    string? Schema,
    IReadOnlyDictionary<string, long> Counts,
    int AttachmentFiles)
{
    /// <summary>Rows of <paramref name="entity"/> (0 when absent).</summary>
    public long Count(string entity) => Counts.TryGetValue(entity, out var n) ? n : 0;
}
