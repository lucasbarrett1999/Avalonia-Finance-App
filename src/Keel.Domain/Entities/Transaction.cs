namespace Keel.Domain.Entities;

/// <summary>
/// A ledger entry. Outflows are negative, inflows positive. Soft-deleted rows
/// (<see cref="IsDeleted"/>) are excluded from all math.
/// </summary>
public class Transaction
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Account the transaction belongs to.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Civil date; there is no time component in the ledger (6.4.6).</summary>
    public DateOnly Date { get; set; }

    /// <summary>Resolved payee.</summary>
    public Guid? PayeeId { get; set; }

    /// <summary>Payee text as entered or imported.</summary>
    public string PayeeRaw { get; set; } = string.Empty;

    /// <summary>Memo.</summary>
    public string? Memo { get; set; }

    /// <summary>Amount in minor units of the account's currency.</summary>
    public long Amount { get; set; }

    /// <summary>Category; null for splits parents, on-budget transfers, and uncategorized rows.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>The other account of a transfer.</summary>
    public Guid? TransferAccountId { get; set; }

    /// <summary>Shared id of the two rows that make up one transfer.</summary>
    public Guid? TransferPairId { get; set; }

    /// <summary>Cleared state.</summary>
    public TransactionStatus Status { get; set; }

    /// <summary>Approved in the review queue (manual entries are approved on insert).</summary>
    public bool IsApproved { get; set; }

    /// <summary>Origin of the transaction.</summary>
    public TransactionSource Source { get; set; }

    /// <summary>Provider transaction id (Plaid transaction_id, OFX FITID, SimpleFIN id).</summary>
    public string? ProviderTransactionId { get; set; }

    /// <summary>Provider id of the pending transaction this posted transaction replaces.</summary>
    public string? ProviderPendingId { get; set; }

    /// <summary>SHA-256 dedup fingerprint (6.5).</summary>
    public string? ImportFingerprint { get; set; }

    /// <summary>Soft-delete flag.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update time (UTC).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Scheduled transaction this instance was entered from.</summary>
    public Guid? ScheduledFromId { get; set; }

    /// <summary>Splits; when present, <see cref="CategoryId"/> is null and the split amounts sum to <see cref="Amount"/>.</summary>
    public ICollection<TransactionSplit> Splits { get; } = new List<TransactionSplit>();

    /// <summary>Whether the transaction is split.</summary>
    public bool IsSplit => Splits.Count > 0;

    /// <summary>Whether the splits (if any) sum exactly to the amount.</summary>
    public bool SplitsBalance => !IsSplit || Splits.Sum(s => s.Amount) == Amount;
}
