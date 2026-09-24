namespace Keel.Domain.Entities;

/// <summary>An append-only change record that powers undo and the activity log.</summary>
public class AuditEvent
{
    /// <summary>Identifier (monotonic).</summary>
    public long Id { get; set; }

    /// <summary>When the change happened (UTC).</summary>
    public DateTime At { get; set; }

    /// <summary>Kind of change.</summary>
    public AuditEventKind Kind { get; set; }

    /// <summary>Entity type name, e.g. "Transaction".</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Entity key as text.</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Entity state before the change as JSON.</summary>
    public string? BeforeJson { get; set; }

    /// <summary>Entity state after the change as JSON.</summary>
    public string? AfterJson { get; set; }
}
