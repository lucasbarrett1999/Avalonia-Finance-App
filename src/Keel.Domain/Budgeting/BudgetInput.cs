using Keel.Domain.Entities;

namespace Keel.Domain.Budgeting;

/// <summary>How a category takes part in the budget math (PRD 6.4).</summary>
public enum BudgetCategoryKind
{
    /// <summary>An ordinary envelope (6.4.2).</summary>
    Regular,

    /// <summary>A category of the system Inflow group ("Ready to Assign"); its activity is income (6.4.1).</summary>
    Inflow,

    /// <summary>The Credit Card Payment category of an on-budget credit account (6.4.5).</summary>
    CreditCardPayment,
}

/// <summary>An account as the budget sees it. Closed accounts are included: they count historically.</summary>
/// <param name="Id">Account id.</param>
/// <param name="Name">Display name (explanations only).</param>
/// <param name="Type">Account type.</param>
/// <param name="IsOnBudget">Tracking (off-budget) accounts never affect the budget (6.3).</param>
/// <param name="IsClosed">Closed flag (informational; closed accounts still count).</param>
public sealed record BudgetAccount(Guid Id, string Name, AccountType Type, bool IsOnBudget, bool IsClosed = false)
{
    /// <summary>On-budget credit account with a Credit Card Payment category (6.4.5).</summary>
    public bool IsBudgetCredit => IsOnBudget && AccountTypeInfo.IsCredit(Type);

    /// <summary>On-budget cash account (holds assignable money).</summary>
    public bool IsBudgetCash => IsOnBudget && !AccountTypeInfo.IsCredit(Type);

    /// <summary>Projects an account entity.</summary>
    public static BudgetAccount From(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new BudgetAccount(account.Id, account.Name, account.Type, account.IsOnBudget, account.IsClosed);
    }
}

/// <summary>A category group.</summary>
/// <param name="Id">Group id.</param>
/// <param name="Name">Display name.</param>
/// <param name="SortOrder">Display order.</param>
/// <param name="IsSystem">System group (Inflow, Credit Card Payments).</param>
/// <param name="IsHidden">Hidden groups hide all their categories.</param>
public sealed record BudgetGroup(Guid Id, string Name, int SortOrder, bool IsSystem = false, bool IsHidden = false)
{
    /// <summary>Projects a group entity.</summary>
    public static BudgetGroup From(CategoryGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new BudgetGroup(group.Id, group.Name, group.SortOrder, group.IsSystem, group.IsHidden);
    }
}

/// <summary>A category as the budget sees it.</summary>
/// <param name="Id">Category id.</param>
/// <param name="GroupId">Owning group.</param>
/// <param name="Name">Display name.</param>
/// <param name="SortOrder">Order within the group.</param>
/// <param name="Kind">Role in the math.</param>
/// <param name="LinkedAccountId">For <see cref="BudgetCategoryKind.CreditCardPayment"/>: the credit account it pays.</param>
/// <param name="IsHidden">Hidden categories still count; they are left out of group rows only (6.4.3).</param>
/// <param name="FlexTag">The user's Flex-mode tag (F-BUD-6); <see cref="FlexKind.Unset"/> means automatic. Not part of the math.</param>
public sealed record BudgetCategory(
    Guid Id,
    Guid GroupId,
    string Name,
    int SortOrder,
    BudgetCategoryKind Kind = BudgetCategoryKind.Regular,
    Guid? LinkedAccountId = null,
    bool IsHidden = false,
    FlexKind FlexTag = FlexKind.Unset)
{
    /// <summary>
    /// Projects a category entity. Categories of the system Inflow group are
    /// <see cref="BudgetCategoryKind.Inflow"/>; a category linked to an on-budget credit account is its
    /// <see cref="BudgetCategoryKind.CreditCardPayment"/> category; everything else is regular.
    /// </summary>
    public static BudgetCategory From(Category category, IReadOnlyDictionary<Guid, BudgetAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(accounts);

        var kind = BudgetCategoryKind.Regular;
        if (category.Id == SystemIds.ReadyToAssignCategory || category.GroupId == SystemIds.InflowGroup)
        {
            kind = BudgetCategoryKind.Inflow;
        }
        else if (category.LinkedAccountId is { } linked && accounts.TryGetValue(linked, out var account) && account.IsBudgetCredit)
        {
            kind = BudgetCategoryKind.CreditCardPayment;
        }

        return new BudgetCategory(category.Id, category.GroupId, category.Name, category.SortOrder, kind, category.LinkedAccountId, category.IsHidden, category.FlexKind);
    }
}

/// <summary>
/// Pre-aggregated activity: Σ Amount of non-deleted transactions and splits in one account, one
/// month, one category (PRD 6.4.9, <c>GROUP BY category, month, account</c>). A null category is
/// uncategorized activity that is not a transfer.
/// </summary>
/// <param name="CategoryId">Category, or null for uncategorized rows.</param>
/// <param name="Month">Any date in the month; normalized to the first day.</param>
/// <param name="AccountId">Account the rows belong to.</param>
/// <param name="Amount">Sum in minor units (outflows negative).</param>
public readonly record struct ActivityTotal(Guid? CategoryId, DateOnly Month, Guid AccountId, long Amount);

/// <summary>An assignment (<see cref="BudgetAssignment"/> row).</summary>
/// <param name="CategoryId">Category.</param>
/// <param name="Month">Any date in the month; normalized to the first day.</param>
/// <param name="Assigned">Assigned amount in minor units.</param>
public readonly record struct AssignmentTotal(Guid CategoryId, DateOnly Month, long Assigned);

/// <summary>
/// Σ of uncategorized transfer rows INTO a credit account from another account in one month
/// (the positive, card-side rows). Only transfers from on-budget cash accounts count as
/// payments (6.4.5); others are ignored by the calculator.
/// </summary>
/// <param name="CardAccountId">The credit account that received the money.</param>
/// <param name="FromAccountId">The account the money came from.</param>
/// <param name="Month">Any date in the month; normalized to the first day.</param>
/// <param name="Amount">Sum in minor units (&gt; 0).</param>
public readonly record struct CardTransferTotal(Guid CardAccountId, Guid FromAccountId, DateOnly Month, long Amount);

/// <summary>Everything <see cref="BudgetCalculator"/> needs; all of it is derivable from the ledger.</summary>
/// <param name="Accounts">All accounts, including closed and tracking ones.</param>
/// <param name="Groups">All category groups.</param>
/// <param name="Categories">All categories, including hidden and system ones.</param>
/// <param name="Activity">Pre-aggregated activity (duplicates of a key are summed).</param>
/// <param name="Assignments">Assignments (duplicates of a key are summed).</param>
/// <param name="CardTransfers">Transfers into credit accounts.</param>
public sealed record BudgetInput(
    IReadOnlyList<BudgetAccount> Accounts,
    IReadOnlyList<BudgetGroup> Groups,
    IReadOnlyList<BudgetCategory> Categories,
    IReadOnlyList<ActivityTotal> Activity,
    IReadOnlyList<AssignmentTotal> Assignments,
    IReadOnlyList<CardTransferTotal> CardTransfers)
{
    /// <summary>The earliest month with any activity, assignment or card transfer, or null when there is none.</summary>
    public DateOnly? EarliestMonth
    {
        get
        {
            var min = int.MaxValue;
            foreach (var a in Activity)
            {
                min = Math.Min(min, BudgetMonth.Index(a.Month));
            }

            foreach (var a in Assignments)
            {
                min = Math.Min(min, BudgetMonth.Index(a.Month));
            }

            foreach (var t in CardTransfers)
            {
                min = Math.Min(min, BudgetMonth.Index(t.Month));
            }

            return min == int.MaxValue ? null : BudgetMonth.FromIndex(min);
        }
    }
}
