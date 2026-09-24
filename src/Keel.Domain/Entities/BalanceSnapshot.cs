namespace Keel.Domain.Entities;

/// <summary>A dated balance for tracking accounts and provider-reported balances (F-ACC-7).</summary>
public class BalanceSnapshot
{
    /// <summary>Account.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Date of the balance.</summary>
    public DateOnly Date { get; set; }

    /// <summary>Balance in minor units.</summary>
    public long Balance { get; set; }

    /// <summary>Who recorded it.</summary>
    public BalanceSource Source { get; set; }
}
