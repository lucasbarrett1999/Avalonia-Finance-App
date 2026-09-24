namespace Keel.Domain.Budgeting;

/// <summary>How a category is overspent (6.4.4). Cash overspending shows red, credit-only yellow.</summary>
public enum BudgetOverspending
{
    /// <summary>Available is zero or more.</summary>
    None,

    /// <summary>Overspent, and all of it is covered by card spending (yellow).</summary>
    Credit,

    /// <summary>Overspent with at least some cash overspending (red).</summary>
    Cash,
}

/// <summary>An amount attributed to an account.</summary>
/// <param name="AccountId">Account.</param>
/// <param name="Amount">Minor units.</param>
public readonly record struct AccountAmount(Guid AccountId, long Amount);

/// <summary>An amount attributed to a category.</summary>
/// <param name="CategoryId">Category.</param>
/// <param name="Amount">Minor units.</param>
public readonly record struct CategoryAmount(Guid CategoryId, long Amount);

/// <summary>
/// One cell of the budget grid: category <see cref="CategoryId"/> in <see cref="Month"/>, with every
/// intermediate value of PRD 6.4 so the math can be shown (principle 7).
/// </summary>
public sealed record CategoryMonthResult
{
    /// <summary>Category.</summary>
    public required Guid CategoryId { get; init; }

    /// <summary>Month (first day).</summary>
    public required DateOnly Month { get; init; }

    /// <summary>Role of the category.</summary>
    public required BudgetCategoryKind Kind { get; init; }

    /// <summary>False when the category or its group is hidden (excluded from group rows only).</summary>
    public required bool IsVisible { get; init; }

    /// <summary>Available(c, M−1), before the carry rule drops negative balances.</summary>
    public required long PreviousAvailable { get; init; }

    /// <summary>Carry(c, M) = max(0, Available(c, M−1)).</summary>
    public required long Carry { get; init; }

    /// <summary>Assigned(c, M).</summary>
    public required long Assigned { get; init; }

    /// <summary>
    /// Activity(c, M). For a Credit Card Payment category this is Σ Covered − Payments (6.4.5).
    /// </summary>
    public required long Activity { get; init; }

    /// <summary>RawAvailable = Carry + Assigned + Activity.</summary>
    public required long RawAvailable { get; init; }

    /// <summary>Available(c, M) (equal to <see cref="RawAvailable"/>; negative means overspent).</summary>
    public required long Available { get; init; }

    /// <summary>Cash part of the overspending (≤ 0); reduces next month's Ready to Assign.</summary>
    public required long CashOverspent { get; init; }

    /// <summary>Credit part of the overspending (≤ 0); leaves the card payment underfunded.</summary>
    public required long CreditOverspent { get; init; }

    /// <summary>CreditSpendingMagnitude(c, M) = Σ_K max(0, −CardActivity(c, K, M)) (regular categories).</summary>
    public long CreditSpending { get; init; }

    /// <summary>
    /// Regular: Σ_K Covered(c, K, M), the card spending moved to payment categories.
    /// Credit Card Payment: Σ_c Covered(c, K, M), the money that arrived from spending categories.
    /// </summary>
    public long Covered { get; init; }

    /// <summary>Payments(K, M) for a Credit Card Payment category (&gt; 0), otherwise 0.</summary>
    public long Payments { get; init; }

    /// <summary>For a Credit Card Payment category: the credit account it pays.</summary>
    public Guid? CardAccountId { get; init; }

    /// <summary>Activity(c, M) by account (regular categories).</summary>
    public IReadOnlyList<AccountAmount> ActivityByAccount { get; init; } = [];

    /// <summary>CardSpend(c, K, M) per credit account with spending (regular categories).</summary>
    public IReadOnlyList<AccountAmount> CardSpendByCard { get; init; } = [];

    /// <summary>Covered(c, K, M) per credit account with spending (regular categories).</summary>
    public IReadOnlyList<AccountAmount> CoveredByCard { get; init; } = [];

    /// <summary>Covered(c, K, M) per spending category (Credit Card Payment categories).</summary>
    public IReadOnlyList<CategoryAmount> CoveredFromCategories { get; init; } = [];

    /// <summary>Payments(K, M) per paying cash account (Credit Card Payment categories).</summary>
    public IReadOnlyList<AccountAmount> PaymentsByAccount { get; init; } = [];

    /// <summary>Overspending state (colour) of the cell.</summary>
    public BudgetOverspending Overspending =>
        CashOverspent < 0 ? BudgetOverspending.Cash
        : CreditOverspent < 0 ? BudgetOverspending.Credit
        : BudgetOverspending.None;
}

/// <summary>A group row: sums of the visible child categories (6.4.3).</summary>
public sealed record GroupMonthResult
{
    /// <summary>Group.</summary>
    public required Guid GroupId { get; init; }

    /// <summary>Hidden groups are still computed; the UI decides whether to show them.</summary>
    public required bool IsHidden { get; init; }

    /// <summary>Σ Assigned of visible child categories.</summary>
    public required long Assigned { get; init; }

    /// <summary>Σ Activity of visible child categories.</summary>
    public required long Activity { get; init; }

    /// <summary>Σ Available of visible child categories.</summary>
    public required long Available { get; init; }

    /// <summary>All child categories in display order, visible or not.</summary>
    public required IReadOnlyList<Guid> CategoryIds { get; init; }
}

/// <summary>One month of the budget: header numbers, groups and categories.</summary>
public sealed record BudgetMonthResult
{
    /// <summary>Month (first day).</summary>
    public required DateOnly Month { get; init; }

    /// <summary>RTA(M) per 6.4.1; may be negative.</summary>
    public required long ReadyToAssign { get; init; }

    /// <summary>Σ_{m ≤ M} InflowRTA(m).</summary>
    public required long InflowThroughMonth { get; init; }

    /// <summary>InflowRTA(M).</summary>
    public required long InflowThisMonth { get; init; }

    /// <summary>Σ_{m ≤ M} TotalAssigned(m).</summary>
    public required long AssignedThroughMonth { get; init; }

    /// <summary>Σ_{m &gt; M} TotalAssigned(m) ("assigned in future months").</summary>
    public required long AssignedInFuture { get; init; }

    /// <summary>Σ_{m &lt; M} Σ_c CashOverspent(c, m) (≤ 0).</summary>
    public required long CashOverspentBefore { get; init; }

    /// <summary>Σ_c CashOverspent(c, M) (≤ 0); reduces the next month's RTA.</summary>
    public required long CashOverspentThisMonth { get; init; }

    /// <summary>TotalAssigned(M) over all non-Inflow categories, hidden ones included.</summary>
    public required long TotalAssigned { get; init; }

    /// <summary>Σ Activity over all non-Inflow categories, hidden ones included.</summary>
    public required long TotalActivity { get; init; }

    /// <summary>Σ Available over all non-Inflow categories, hidden ones included.</summary>
    public required long TotalAvailable { get; init; }

    /// <summary>
    /// On-budget activity outside every envelope: uncategorized non-transfer rows and rows
    /// categorized directly to a Credit Card Payment category. Not part of any 6.4 formula;
    /// surfaced so the UI can prompt the user to categorize it (ADR 0005).
    /// </summary>
    public required long UncategorizedActivity { get; init; }

    /// <summary>Category groups in display order (the Inflow group is excluded).</summary>
    public required IReadOnlyList<GroupMonthResult> Groups { get; init; }

    /// <summary>Every non-Inflow category in display order.</summary>
    public required IReadOnlyList<CategoryMonthResult> Categories { get; init; }

    /// <summary>The cell of <paramref name="categoryId"/>.</summary>
    /// <exception cref="KeyNotFoundException">The category is not in the budget.</exception>
    public CategoryMonthResult Category(Guid categoryId) =>
        Categories.FirstOrDefault(c => c.CategoryId == categoryId)
        ?? throw new KeyNotFoundException($"Category {categoryId} is not in the budget.");
}
