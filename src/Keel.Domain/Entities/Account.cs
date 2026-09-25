namespace Keel.Domain.Entities;

/// <summary>A financial account (F-ACC-1). The ledger balance is always derived from transactions.</summary>
public class Account
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; } = EntityIds.New();

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Account type; drives classification (6.3).</summary>
    public AccountType Type { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = Keel.Domain.Currency.Default;

    /// <summary>On-budget flag; defaults by type and may be overridden for Savings and Cash only.</summary>
    public bool IsOnBudget { get; set; }

    /// <summary>Closed accounts hide from sidebars but remain in reports.</summary>
    public bool IsClosed { get; set; }

    /// <summary>Order within the account's sidebar group.</summary>
    public int SortOrder { get; set; }

    /// <summary>Date of the opening balance.</summary>
    public DateOnly OpeningDate { get; set; }

    /// <summary>Free-text notes.</summary>
    public string? Notes { get; set; }

    /// <summary>Owner profile (household, P2).</summary>
    public Guid? OwnerProfileId { get; set; }

    /// <summary>Sync connection this account is linked to, if any.</summary>
    public Guid? SyncConnectionId { get; set; }

    /// <summary>The provider's account id within the sync connection.</summary>
    public string? ProviderAccountId { get; set; }

    /// <summary>Latest balance reported by the provider, in minor units.</summary>
    public long? ReportedBalance { get; set; }

    /// <summary>When the provider reported <see cref="ReportedBalance"/> (UTC).</summary>
    public DateTime? ReportedBalanceAt { get; set; }

    /// <summary>
    /// Annual interest rate of a debt in basis points (1999 = 19.99% APR), for the debt payoff planner
    /// (F-GOAL-2); null when unknown. Only liability accounts carry it.
    /// </summary>
    public int? InterestRateBps { get; set; }

    /// <summary>Minimum monthly payment of a debt in minor units (F-GOAL-2); null when unknown.</summary>
    public long? MinimumPayment { get; set; }

    /// <summary>Creates an account whose on-budget flag follows the type default (6.3).</summary>
    public static Account Create(string name, AccountType type, DateOnly openingDate, string currency = Keel.Domain.Currency.Default) => new()
    {
        Name = name,
        Type = type,
        Currency = Keel.Domain.Currency.Normalize(currency),
        IsOnBudget = AccountTypeInfo.IsOnBudgetByDefault(type),
        OpeningDate = openingDate,
    };

    /// <summary>Sidebar group of this account.</summary>
    public AccountGroup Group => AccountTypeInfo.GroupOf(Type, IsOnBudget);
}
