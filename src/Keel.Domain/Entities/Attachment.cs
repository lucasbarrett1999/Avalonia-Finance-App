namespace Keel.Domain.Entities;

/// <summary>A receipt or document attached to a transaction, stored by hash next to the budget file.</summary>
public class Attachment
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Transaction.</summary>
    public Guid TransactionId { get; set; }

    /// <summary>Original file name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the content; also the stored file name.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>MIME type.</summary>
    public string MimeType { get; set; } = "application/octet-stream";

    /// <summary>Transaction.</summary>
    public Transaction? Transaction { get; set; }
}
