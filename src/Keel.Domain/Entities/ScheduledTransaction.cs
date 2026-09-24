namespace Keel.Domain.Entities;

/// <summary>A transaction template with a recurrence rule (F-ACC-6).</summary>
public class ScheduledTransaction
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Account.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Amount in minor units.</summary>
    public long Amount { get; set; }

    /// <summary>Payee.</summary>
    public Guid PayeeId { get; set; }

    /// <summary>Category.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>Transfer target account.</summary>
    public Guid? TransferAccountId { get; set; }

    /// <summary>Memo.</summary>
    public string? Memo { get; set; }

    /// <summary>Recurrence rule (RFC 5545 subset).</summary>
    public string RecurrenceRule { get; set; } = string.Empty;

    /// <summary>Next instance date.</summary>
    public DateOnly NextDate { get; set; }

    /// <summary>Last possible instance date.</summary>
    public DateOnly? EndDate { get; set; }

    /// <summary>Enter instances automatically on their date instead of prompting.</summary>
    public bool AutoEnter { get; set; }
}
