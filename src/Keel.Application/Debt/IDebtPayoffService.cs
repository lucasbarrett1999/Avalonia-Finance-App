using Keel.Domain;
using Keel.Domain.Debt;

namespace Keel.Application.Debt;

/// <summary>
/// The debt payoff planner (F-GOAL-2, ADR 0093): feeds <see cref="DebtPayoffCalculator"/> from the
/// accounts (balance owed, interest rate, minimum payment) and turns a plan into debt-payment targets
/// on each debt's payment category through the budget service.
/// </summary>
public interface IDebtPayoffService
{
    /// <summary>The debts and a plan with <paramref name="extraPerMonth"/> on top of the minimums, ordered by <paramref name="ordering"/>.</summary>
    Task<DebtPayoffOverview> GetPlanAsync(long extraPerMonth, DebtOrdering ordering, CancellationToken ct);

    /// <summary>
    /// Sets a debt-payment target equal to this month's planned payment on each planned debt's payment
    /// category (creating one named by <paramref name="names"/> when a loan has none),
    /// all targets as one undoable budget action.
    /// </summary>
    Task<DebtTargetsResult> SetPaymentTargetsAsync(long extraPerMonth, DebtOrdering ordering, NewPaymentCategory names, CancellationToken ct);
}

/// <summary>Where and how a payment category is created for a loan that has none (user-visible, so the UI supplies localized text).</summary>
/// <param name="GroupName">Group to create it in (created when missing), e.g. "Debt payments".</param>
/// <param name="NameFormat">Composite format with the account name as {0}, e.g. "{0} payment".</param>
public sealed record NewPaymentCategory(string GroupName, string NameFormat);

/// <summary>A liability account the planner knows about.</summary>
/// <param name="AccountId">Account.</param>
/// <param name="Name">Account name.</param>
/// <param name="Type">Account type.</param>
/// <param name="IsOnBudget">On budget (a card or line of credit) or a tracking loan.</param>
/// <param name="Owed">Amount owed now, positive (the ledger balance, or the latest snapshot plus later activity for a tracking account with snapshots).</param>
/// <param name="InterestRateBps">Annual rate in basis points, or null when not entered.</param>
/// <param name="MinimumPayment">Minimum monthly payment, or null when not entered.</param>
/// <param name="PaymentCategoryId">The category its payments are budgeted in, or null when none is known yet.</param>
/// <param name="PaymentCategoryName">That category's name.</param>
/// <param name="CurrentTarget">The amount of the category's current debt-payment target for this account, if any.</param>
public sealed record DebtAccountDto(
    Guid AccountId,
    string Name,
    AccountType Type,
    bool IsOnBudget,
    long Owed,
    int? InterestRateBps,
    long? MinimumPayment,
    Guid? PaymentCategoryId,
    string? PaymentCategoryName,
    long? CurrentTarget)
{
    /// <summary>Whether both the rate and the minimum payment are known (the debt can be planned).</summary>
    public bool HasTerms => InterestRateBps is not null && MinimumPayment is not null;

    /// <summary>The planner's input for this debt.</summary>
    public DebtInput ToInput() => new(AccountId, Name, Owed, InterestRateBps ?? 0, MinimumPayment ?? 0);
}

/// <summary>What the planner shows.</summary>
/// <param name="Currency">Budget currency.</param>
/// <param name="StartMonth">Month 1 of the plans (this month).</param>
/// <param name="Debts">Debts with a rate and a minimum, in the plan's priority order.</param>
/// <param name="MissingTerms">Debts still missing a rate or a minimum payment (left out of the plans).</param>
/// <param name="Plan">The plan with the extra and ordering.</param>
/// <param name="MinimumOnly">Every debt on its own at its minimum ("at current payment"), in <see cref="Debts"/> order.</param>
public sealed record DebtPayoffOverview(
    string Currency,
    DateOnly StartMonth,
    IReadOnlyList<DebtAccountDto> Debts,
    IReadOnlyList<DebtAccountDto> MissingTerms,
    DebtPlan Plan,
    DebtPlan MinimumOnly)
{
    /// <summary>Whether any liability account is owed at all.</summary>
    public bool HasDebts => Debts.Count > 0 || MissingTerms.Count > 0;

    /// <summary>The calendar month of plan month <paramref name="months"/> (1 = <see cref="StartMonth"/>).</summary>
    public DateOnly MonthOf(int months) => StartMonth.AddMonths(Math.Max(0, months - 1));
}

/// <summary>Result of <see cref="IDebtPayoffService.SetPaymentTargetsAsync"/>.</summary>
/// <param name="TargetsSet">Debt-payment targets set or updated.</param>
/// <param name="CategoriesCreated">Payment categories created for loans without one.</param>
public sealed record DebtTargetsResult(int TargetsSet, int CategoriesCreated);
