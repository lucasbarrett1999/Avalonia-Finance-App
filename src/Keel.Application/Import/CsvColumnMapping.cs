namespace Keel.Application.Import;

/// <summary>How amounts are laid out in a CSV file.</summary>
public enum CsvAmountLayout
{
    /// <summary>One signed amount column.</summary>
    SignedAmount,

    /// <summary>Separate debit (outflow) and credit (inflow) columns holding magnitudes.</summary>
    DebitCredit,

    /// <summary>An unsigned amount column plus a type column (DEBIT/CREDIT, Withdrawal/Deposit).</summary>
    AmountWithType,
}

/// <summary>Sign convention of a signed amount column.</summary>
public enum CsvSignConvention
{
    /// <summary>Inflows positive, outflows negative (Keel's own convention; most bank exports).</summary>
    InflowPositive,

    /// <summary>Outflows positive (some credit-card exports list purchases as positive).</summary>
    OutflowPositive,
}

/// <summary>
/// Which CSV column holds what, and how to read it. The CSV mapping dialog edits this and it is
/// remembered per account; parsing with an explicit mapping is deterministic. Column indexes
/// are zero-based.
/// </summary>
public sealed record CsvColumnMapping
{
    /// <summary>Field delimiter: <c>,</c> <c>;</c> tab or <c>|</c>.</summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>Number of non-blank records before the header row (or before the first data row
    /// when there is no header): bank preambles, account summaries.</summary>
    public int SkipRows { get; init; }

    /// <summary>Whether the first record after <see cref="SkipRows"/> is a header row.</summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>Date column.</summary>
    public required int DateColumn { get; init; }

    /// <summary>A date format name from the built-in list (<c>MM/dd/yyyy</c>, <c>dd/MM/yyyy</c>,
    /// <c>yyyy-MM-dd</c>, <c>MMM d, yyyy</c>, ...) or any .NET exact date format.</summary>
    public required string DateFormat { get; init; }

    /// <summary>Payee or description column.</summary>
    public int? PayeeColumn { get; init; }

    /// <summary>Memo column.</summary>
    public int? MemoColumn { get; init; }

    /// <summary>Amount layout.</summary>
    public CsvAmountLayout AmountLayout { get; init; } = CsvAmountLayout.SignedAmount;

    /// <summary>Amount column (<see cref="CsvAmountLayout.SignedAmount"/> and <see cref="CsvAmountLayout.AmountWithType"/>).</summary>
    public int? AmountColumn { get; init; }

    /// <summary>Debit (outflow) column (<see cref="CsvAmountLayout.DebitCredit"/>).</summary>
    public int? DebitColumn { get; init; }

    /// <summary>Credit (inflow) column (<see cref="CsvAmountLayout.DebitCredit"/>).</summary>
    public int? CreditColumn { get; init; }

    /// <summary>Type column: required for <see cref="CsvAmountLayout.AmountWithType"/>, otherwise
    /// carried as <see cref="ParsedTransaction.TransactionType"/>.</summary>
    public int? TypeColumn { get; init; }

    /// <summary>Sign convention of the amount column for <see cref="CsvAmountLayout.SignedAmount"/>.</summary>
    public CsvSignConvention SignConvention { get; init; } = CsvSignConvention.InflowPositive;

    /// <summary>Decimal separator of amounts: <c>.</c> or <c>,</c>.</summary>
    public char DecimalSeparator { get; init; } = '.';

    /// <summary>Bank transaction id or reference column.</summary>
    public int? IdColumn { get; init; }

    /// <summary>Check number column.</summary>
    public int? CheckNumberColumn { get; init; }

    /// <summary>Category column (a hint carried to the pipeline).</summary>
    public int? CategoryColumn { get; init; }

    /// <summary>Status column; values such as <c>Pending</c> mark the row pending.</summary>
    public int? StatusColumn { get; init; }

    /// <summary>Currency column holding ISO codes per row.</summary>
    public int? CurrencyColumn { get; init; }
}

/// <summary>The CSV layout a parse used.</summary>
/// <param name="Mapping">The mapping (detected, or the one given in <see cref="ImportOptions.CsvMapping"/>).</param>
/// <param name="Headers">Header names, or <c>Column 1</c>, <c>Column 2</c>, ... when the file has none.</param>
/// <param name="DateFormatCandidates">Every built-in date format that fits all rows.</param>
/// <param name="IsDateFormatAmbiguous">True when both month-first and day-first fit and give
/// different dates; the UI should ask and re-parse with <see cref="ImportOptions.PreferredDateOrder"/>
/// or an explicit mapping.</param>
public sealed record DetectedCsvLayout(
    CsvColumnMapping Mapping,
    IReadOnlyList<string> Headers,
    IReadOnlyList<string> DateFormatCandidates,
    bool IsDateFormatAmbiguous);
