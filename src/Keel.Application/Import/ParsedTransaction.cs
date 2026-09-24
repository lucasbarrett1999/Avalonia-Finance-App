using Keel.Domain.Import;

namespace Keel.Application.Import;

/// <summary>
/// One transaction read from an import file (CSV, OFX/QFX, QIF), before normalization and
/// deduplication. Amounts are minor units with outflows negative (PRD 6.1).
/// </summary>
public sealed record ParsedTransaction : IImportRecord
{
    /// <summary>Ledger date as the file states it (no time-zone conversion).</summary>
    public required DateOnly Date { get; init; }

    /// <summary>Signed amount in minor units of <see cref="Currency"/>; outflows negative.</summary>
    public required long Amount { get; init; }

    /// <summary>Payee exactly as the file spells it (empty when the file has none).</summary>
    public string PayeeRaw { get; init; } = string.Empty;

    /// <summary>Memo or extended description.</summary>
    public string? Memo { get; init; }

    /// <summary>File transaction id: OFX <c>FITID</c>, or a CSV id/reference column.</summary>
    public string? ProviderTransactionId { get; init; }

    /// <summary>For posted rows from a provider: the id of the pending row this one replaces.</summary>
    public string? PendingTransactionId { get; init; }

    /// <summary>True when the source marks the row as pending (not yet posted).</summary>
    public bool IsPending { get; init; }

    /// <summary>Check or reference number (OFX <c>CHECKNUM</c>, QIF <c>N</c>, CSV check column).</summary>
    public string? CheckNumber { get; init; }

    /// <summary>ISO 4217 currency of <see cref="Amount"/>.</summary>
    public string Currency { get; init; } = Keel.Domain.Currency.Default;

    /// <summary>The source's transaction type (OFX <c>TRNTYPE</c>, CSV type column).</summary>
    public string? TransactionType { get; init; }

    /// <summary>The source's category (QIF <c>L</c>, CSV category column); a hint for rules, never applied blindly.</summary>
    public string? Category { get; init; }

    /// <summary>Account the row belongs to in multi-account files (OFX <c>ACCTID</c>, QIF account name).</summary>
    public string? SourceAccountId { get; init; }

    /// <summary>Splits carried by the file (QIF <c>S</c>/<c>E</c>/<c>$</c>).</summary>
    public IReadOnlyList<ParsedSplit> Splits { get; init; } = [];

    /// <summary>Every other field the file had for this row, keyed by the file's own field name.</summary>
    public IReadOnlyDictionary<string, string> Extras { get; init; } = EmptyExtras;

    /// <summary>Zero-based position among the transactions of this parse.</summary>
    public int SourceIndex { get; init; }

    /// <summary>One-based line in the decoded file where the row starts.</summary>
    public int SourceLine { get; init; }

    private static readonly IReadOnlyDictionary<string, string> EmptyExtras = new Dictionary<string, string>();
}

/// <summary>One split line of a parsed transaction.</summary>
/// <param name="Category">The file's category for the split.</param>
/// <param name="Memo">Split memo.</param>
/// <param name="Amount">Signed minor units.</param>
public sealed record ParsedSplit(string? Category, string? Memo, long Amount);
