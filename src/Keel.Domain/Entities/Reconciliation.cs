namespace Keel.Domain.Entities;

/// <summary>A reconciliation event (F-ACC-3).</summary>
public class Reconciliation
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Account.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Statement date.</summary>
    public DateOnly StatementDate { get; set; }

    /// <summary>Statement balance in minor units.</summary>
    public long StatementBalance { get; set; }

    /// <summary>When the reconciliation was finished (UTC); null while in progress.</summary>
    public DateTime? CompletedAt { get; set; }
}
