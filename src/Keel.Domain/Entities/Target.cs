namespace Keel.Domain.Entities;

/// <summary>A category target (F-BUD-4). At most one per category.</summary>
public class Target
{
    /// <summary>Category (primary key).</summary>
    public Guid CategoryId { get; set; }

    /// <summary>Target type.</summary>
    public TargetType Type { get; set; }

    /// <summary>Target amount in minor units.</summary>
    public long Amount { get; set; }

    /// <summary>Target date for savings-balance targets.</summary>
    public DateOnly? TargetDate { get; set; }

    /// <summary>Cadence for repeating targets.</summary>
    public RecurrenceCadence? Cadence { get; set; }

    /// <summary>Linked loan or credit account for debt-payment targets.</summary>
    public Guid? LinkedAccountId { get; set; }
}
