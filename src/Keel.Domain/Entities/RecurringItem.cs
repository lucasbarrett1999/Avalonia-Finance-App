namespace Keel.Domain.Entities;

/// <summary>A detected or user-created recurring bill, subscription, or paycheck (F-REC-1).</summary>
public class RecurringItem
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Payee.</summary>
    public Guid PayeeId { get; set; }

    /// <summary>Account the item recurs in.</summary>
    public Guid? AccountId { get; set; }

    /// <summary>Cadence.</summary>
    public RecurrenceCadence Cadence { get; set; }

    /// <summary>Expected amount in minor units.</summary>
    public long ExpectedAmount { get; set; }

    /// <summary>Amount tolerance in minor units.</summary>
    public long AmountTolerance { get; set; }

    /// <summary>Whether the amount varies beyond tolerance while the cadence holds (6.6).</summary>
    public bool IsVariableAmount { get; set; }

    /// <summary>Next expected date.</summary>
    public DateOnly NextExpectedDate { get; set; }

    /// <summary>Date of the last occurrence.</summary>
    public DateOnly LastSeenDate { get; set; }

    /// <summary>Detection confidence in [0, 1].</summary>
    public double Confidence { get; set; }

    /// <summary>Lifecycle status.</summary>
    public RecurringStatus Status { get; set; }

    /// <summary>Subscription (vs bill).</summary>
    public bool IsSubscription { get; set; }

    /// <summary>Category.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>Scheduled transaction created from this item.</summary>
    public Guid? ScheduledTransactionId { get; set; }
}
