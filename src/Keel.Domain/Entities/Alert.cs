namespace Keel.Domain.Entities;

/// <summary>A notification-center entry (F-REC-3). Alerts never leave the machine.</summary>
public class Alert
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Kind.</summary>
    public AlertKind Kind { get; set; }

    /// <summary>Related recurring item.</summary>
    public Guid? RecurringItemId { get; set; }

    /// <summary>Related transaction.</summary>
    public Guid? TransactionId { get; set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the user read it (UTC).</summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>When the user dismissed it (UTC).</summary>
    public DateTime? DismissedAt { get; set; }

    /// <summary>Kind-specific payload as JSON.</summary>
    public string PayloadJson { get; set; } = "{}";
}
