namespace Keel.Domain.Entities;

/// <summary>A sub-split of a transaction (F-ACC-5).</summary>
public class TransactionSplit
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Parent transaction.</summary>
    public Guid TransactionId { get; set; }

    /// <summary>Category of this split.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>Transfer target account of this split.</summary>
    public Guid? TransferAccountId { get; set; }

    /// <summary>Memo.</summary>
    public string? Memo { get; set; }

    /// <summary>Amount in minor units.</summary>
    public long Amount { get; set; }

    /// <summary>Parent transaction.</summary>
    public Transaction? Transaction { get; set; }
}
