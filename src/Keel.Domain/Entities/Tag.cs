namespace Keel.Domain.Entities;

/// <summary>A free-form tag (F-TXN-8).</summary>
public class Tag
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Tag name; unique.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>Association between a transaction and a tag.</summary>
public class TransactionTag
{
    /// <summary>Transaction.</summary>
    public Guid TransactionId { get; set; }

    /// <summary>Tag.</summary>
    public Guid TagId { get; set; }

    /// <summary>Transaction.</summary>
    public Transaction? Transaction { get; set; }
}
