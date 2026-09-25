namespace Keel.Application.Import;

/// <summary>
/// Reads one import file format into <see cref="ParsedTransaction"/> rows (F-TXN-2). Parsers are
/// pure: they never touch the database and return the same result for the same bytes and options.
/// </summary>
public interface IFileImportParser
{
    /// <summary>
    /// Returns true when this parser recognizes the file by its name or its first bytes.
    /// </summary>
    /// <param name="fileName">File name (extension is a hint, not a requirement).</param>
    /// <param name="head">The first few kilobytes of the file.</param>
    bool CanParse(string fileName, ReadOnlySpan<byte> head);

    /// <summary>Parses the whole stream. Problems with single rows become warnings, not exceptions.</summary>
    Task<ParseResult> ParseAsync(Stream stream, ImportOptions options, CancellationToken ct = default);
}

/// <summary>Picks the parser for a file.</summary>
public interface IFileImportParserResolver
{
    /// <summary>The first parser that can read the file, or null when none can.</summary>
    IFileImportParser? Resolve(string fileName, ReadOnlySpan<byte> head);
}

/// <summary>Import file formats.</summary>
public enum ImportFileFormat
{
    /// <summary>Comma-, semicolon-, tab- or pipe-separated values.</summary>
    Csv,

    /// <summary>Open Financial Exchange, SGML (1.x) or XML (2.x).</summary>
    Ofx,

    /// <summary>Quicken's OFX variant (Web Connect).</summary>
    Qfx,

    /// <summary>Quicken Interchange Format.</summary>
    Qif,

    /// <summary>A YNAB register export (every account in one CSV, with categories and cleared state).</summary>
    Ynab,

    /// <summary>A YNAB budget export (assigned, activity and available per category and month).</summary>
    YnabBudget,

    /// <summary>A Monarch transactions export (every account in one CSV, with categories and tags).</summary>
    Monarch,
}

/// <summary>Day/month order for numeric dates such as 03/04/2026.</summary>
public enum DateOrder
{
    /// <summary>US style: month first (MM/dd/yyyy).</summary>
    MonthFirst,

    /// <summary>European style: day first (dd/MM/yyyy).</summary>
    DayFirst,
}

/// <summary>Options for one parse.</summary>
public sealed record ImportOptions
{
    /// <summary>Defaults: USD, auto-detected CSV layout and encoding.</summary>
    public static ImportOptions Default { get; } = new();

    /// <summary>Currency of the target account; used when the file does not state one.</summary>
    public string Currency { get; init; } = Keel.Domain.Currency.Default;

    /// <summary>An explicit CSV mapping (from the mapping dialog or remembered per account).
    /// When set, CSV layout detection is skipped and parsing is fully determined by it.</summary>
    public CsvColumnMapping? CsvMapping { get; init; }

    /// <summary>Order to use when numeric dates cannot be disambiguated (the user's answer).
    /// When null the parser assumes month first and reports <see cref="ImportWarningCode.AmbiguousDateFormat"/>.</summary>
    public DateOrder? PreferredDateOrder { get; init; }

    /// <summary>Forces a text encoding by web name (<c>utf-8</c>, <c>windows-1252</c>, <c>utf-16</c>).</summary>
    public string? EncodingName { get; init; }
}

/// <summary>Everything a parser found in a file.</summary>
/// <param name="Format">Detected format.</param>
/// <param name="Transactions">Parsed rows, in file order.</param>
/// <param name="Accounts">Accounts the file describes (OFX statements, QIF account blocks).</param>
/// <param name="Warnings">Row-level and file-level problems.</param>
/// <param name="CsvLayout">For CSV: the layout used (detected or given).</param>
/// <param name="EncodingName">Web name of the text encoding used to decode the file.</param>
public sealed record ParseResult(
    ImportFileFormat Format,
    IReadOnlyList<ParsedTransaction> Transactions,
    IReadOnlyList<DetectedAccount> Accounts,
    IReadOnlyList<ImportWarning> Warnings,
    DetectedCsvLayout? CsvLayout = null,
    string? EncodingName = null)
{
    /// <summary>The first (usually only) account in the file.</summary>
    public DetectedAccount? Account => Accounts.Count > 0 ? Accounts[0] : null;

    /// <summary>Budget rows of a budget export (<see cref="ImportFileFormat.YnabBudget"/>); empty for transaction files.</summary>
    public IReadOnlyList<ParsedBudgetRow> BudgetRows { get; init; } = [];

    /// <summary>Whether the file comes from another budgeting app and holds several accounts, categories or a budget
    /// (YNAB, Monarch): it is imported through <c>IMigrationImportService</c> rather than into one account.</summary>
    public bool IsMigration => Format is ImportFileFormat.Ynab or ImportFileFormat.YnabBudget or ImportFileFormat.Monarch;
}

/// <summary>One category and month of a budget export.</summary>
/// <param name="Month">First day of the month.</param>
/// <param name="Group">Category group name as the file spells it.</param>
/// <param name="Category">Category name as the file spells it.</param>
/// <param name="Assigned">Amount assigned (YNAB "Budgeted"/"Assigned") in minor units.</param>
/// <param name="Activity">Activity in minor units, when the file states it.</param>
/// <param name="Available">Available in minor units, when the file states it.</param>
/// <param name="SourceLine">One-based line in the decoded file.</param>
public sealed record ParsedBudgetRow(DateOnly Month, string Group, string Category, long Assigned, long? Activity, long? Available, int SourceLine);

/// <summary>Account information stated by a file.</summary>
public sealed record DetectedAccount
{
    /// <summary>Account number or id as the file states it (OFX <c>ACCTID</c>).</summary>
    public string? AccountId { get; init; }

    /// <summary>Routing or bank id (OFX <c>BANKID</c>).</summary>
    public string? BankId { get; init; }

    /// <summary>Account name (QIF <c>!Account</c> <c>N</c>).</summary>
    public string? Name { get; init; }

    /// <summary>The file's account type (OFX <c>ACCTTYPE</c> or <c>CREDITCARD</c>, QIF <c>Bank</c>/<c>CCard</c>/...).</summary>
    public string? AccountType { get; init; }

    /// <summary>ISO 4217 currency (OFX <c>CURDEF</c>).</summary>
    public string? Currency { get; init; }

    /// <summary>Ledger balance in minor units (OFX <c>LEDGERBAL</c>).</summary>
    public long? LedgerBalance { get; init; }

    /// <summary>Date of <see cref="LedgerBalance"/>.</summary>
    public DateOnly? LedgerBalanceDate { get; init; }

    /// <summary>Available balance in minor units (OFX <c>AVAILBAL</c>).</summary>
    public long? AvailableBalance { get; init; }

    /// <summary>Statement start date (OFX <c>DTSTART</c>).</summary>
    public DateOnly? StatementStart { get; init; }

    /// <summary>Statement end date (OFX <c>DTEND</c>).</summary>
    public DateOnly? StatementEnd { get; init; }
}

/// <summary>Kinds of parse warnings; the UI maps each to a localized message.</summary>
public enum ImportWarningCode
{
    /// <summary>Day and month order cannot be determined from the data; the user must choose.</summary>
    AmbiguousDateFormat,

    /// <summary>A row's date could not be parsed; the row was skipped.</summary>
    InvalidDate,

    /// <summary>A row's amount could not be parsed or was empty; the row was skipped.</summary>
    InvalidAmount,

    /// <summary>A row was skipped for another reason (summary or footer line).</summary>
    SkippedRow,

    /// <summary>A type column value was not recognized as a debit or a credit; the amount's own sign was kept.</summary>
    UnknownTransactionType,

    /// <summary>The CSV sign convention was inferred from the data and should be confirmed.</summary>
    SignConventionGuessed,

    /// <summary>The same provider id (FITID) appears more than once in the file.</summary>
    DuplicateProviderId,

    /// <summary>The declared or detected encoding did not fit the bytes; a fallback was used.</summary>
    EncodingFallback,

    /// <summary>The file's structure is broken (missing closing tags, truncated record).</summary>
    MalformedMarkup,

    /// <summary>A section of the file was ignored (for example QIF investment or list sections).</summary>
    UnsupportedSection,

    /// <summary>The CSV layout could not be detected or the given mapping does not fit the file.</summary>
    InvalidMapping,

    /// <summary>The file contains no transactions.</summary>
    NoTransactions,

    /// <summary>An update would change a reconciled transaction; the stored row was left unchanged.</summary>
    ReconciledNotUpdated,

    /// <summary>A row's currency differs from the account's; the row was skipped.</summary>
    CurrencyMismatch,

    /// <summary>The file describes several accounts; only the chosen one was imported.</summary>
    OtherAccountsInFile,
}

/// <summary>A parse warning.</summary>
/// <param name="Code">Kind of problem.</param>
/// <param name="Message">English diagnostic text; never contains payees or amounts.</param>
/// <param name="Line">One-based line in the decoded file, when the problem is on one line.</param>
/// <param name="Candidates">Choices for the user, e.g. both date formats for <see cref="ImportWarningCode.AmbiguousDateFormat"/>.</param>
public sealed record ImportWarning(ImportWarningCode Code, string Message, int? Line = null, IReadOnlyList<string>? Candidates = null);
