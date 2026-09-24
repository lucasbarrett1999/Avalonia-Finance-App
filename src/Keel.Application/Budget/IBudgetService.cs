using Keel.Domain;
using Keel.Domain.Budgeting;

namespace Keel.Application.Budget;

/// <summary>
/// Envelope-budget use cases (F-BUD-*) on top of <see cref="BudgetCalculator"/>. Every mutation
/// records an <c>AuditEvent</c> with before/after state and publishes
/// <see cref="Messaging.BudgetChanged"/>.
/// </summary>
public interface IBudgetService
{
    /// <summary>Computes the budget grid for a month (any date in the month).</summary>
    Task<BudgetMonthDto> GetMonthAsync(DateOnly month, CancellationToken ct);

    /// <summary>Computes the budget grid for every month from <paramref name="from"/> to <paramref name="to"/>.</summary>
    Task<IReadOnlyList<BudgetMonthDto>> GetRangeAsync(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>
    /// "How is this computed?" (PRD 9.3 inspector): the breakdown of Available for a category, or
    /// of Ready to Assign when <paramref name="categoryId"/> is null.
    /// </summary>
    Task<BudgetExplanationDto> ExplainAsync(Guid? categoryId, DateOnly month, CancellationToken ct);

    /// <summary>Sets Assigned for a category and month (absent row means 0).</summary>
    Task AssignAsync(Guid categoryId, DateOnly month, long assigned, CancellationToken ct);

    /// <summary>Moves Available between categories or to/from Ready to Assign (F-BUD-3).</summary>
    Task MoveMoneyAsync(MoveMoneyRequest request, CancellationToken ct);

    /// <summary>The target of a category, or null.</summary>
    Task<TargetDto?> GetTargetAsync(Guid categoryId, CancellationToken ct);

    /// <summary>Creates or replaces the target of a category (F-BUD-4).</summary>
    Task SetTargetAsync(TargetDto target, CancellationToken ct);

    /// <summary>Removes the target of a category (no-op when there is none).</summary>
    Task DeleteTargetAsync(Guid categoryId, CancellationToken ct);

    /// <summary>Assigns what every underfunded target needs, limited by Ready to Assign (F-BUD-4).</summary>
    Task<FundTargetsResult> FundTargetsAsync(DateOnly month, CancellationToken ct);

    /// <summary>Values for the quick-assign actions of a category (F-BUD-5).</summary>
    Task<QuickAssignDto> GetQuickAssignAsync(Guid categoryId, DateOnly month, CancellationToken ct);

    /// <summary>
    /// Loads the ledger-derived inputs of the budget (accounts, groups, categories, aggregated
    /// activity, card payments, and card balances for <paramref name="from"/>..<paramref name="to"/>)
    /// once. A screen keeps the result and recomputes with the overloads below after assignment or
    /// target changes, which read only assignments and targets (ADR 0040); it loads again after
    /// <see cref="Messaging.LedgerChanged"/>.
    /// </summary>
    Task<BudgetLedgerData> LoadLedgerAsync(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Computes the grid from loaded ledger data plus the current assignments and targets (months within the loaded range).</summary>
    Task<IReadOnlyList<BudgetMonthDto>> GetRangeAsync(BudgetLedgerData ledger, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary><see cref="ExplainAsync(Guid?, DateOnly, CancellationToken)"/> over loaded ledger data.</summary>
    Task<BudgetExplanationDto> ExplainAsync(BudgetLedgerData ledger, Guid? categoryId, DateOnly month, CancellationToken ct);

    /// <summary><see cref="GetQuickAssignAsync(Guid, DateOnly, CancellationToken)"/> over loaded ledger data.</summary>
    Task<QuickAssignDto> GetQuickAssignAsync(BudgetLedgerData ledger, Guid categoryId, DateOnly month, CancellationToken ct);

    /// <summary>The free-text note of a month (F-BUD-7), or null.</summary>
    Task<string?> GetMonthNoteAsync(DateOnly month, CancellationToken ct);

    /// <summary>Sets or clears the note of a month (F-BUD-7); audited, undoable, publishes <see cref="Messaging.BudgetChanged"/>.</summary>
    Task SetMonthNoteAsync(DateOnly month, string? note, CancellationToken ct);
}

/// <summary>How a category is overspent (6.4.4); drives red/yellow colouring.</summary>
public enum OverspendingKind
{
    /// <summary>Available is zero or more.</summary>
    None,

    /// <summary>Only credit overspending (yellow).</summary>
    Credit,

    /// <summary>Some cash overspending (red).</summary>
    Cash,
}

/// <summary>One month of the budget grid.</summary>
/// <param name="Month">Month (first day).</param>
/// <param name="ReadyToAssign">RTA(M); negative shows the "assigned more than you have" banner.</param>
/// <param name="TotalAssigned">Σ Assigned over all categories.</param>
/// <param name="TotalActivity">Σ Activity over all categories.</param>
/// <param name="TotalAvailable">Σ Available over all categories.</param>
/// <param name="Groups">Group rows in display order (the Inflow group is not shown).</param>
/// <param name="AssignedInFuture">Assigned in later months (already subtracted from RTA).</param>
/// <param name="UncategorizedActivity">On-budget activity with no envelope; prompt the user to categorize it.</param>
public sealed record BudgetMonthDto(
    DateOnly Month,
    Money ReadyToAssign,
    Money TotalAssigned,
    Money TotalActivity,
    Money TotalAvailable,
    IReadOnlyList<BudgetGroupDto> Groups,
    Money AssignedInFuture,
    Money UncategorizedActivity);

/// <summary>A category group row with totals of its visible categories.</summary>
/// <param name="Id">Group id.</param>
/// <param name="Name">Name.</param>
/// <param name="IsSystem">System group (Credit Card Payments).</param>
/// <param name="Assigned">Σ Assigned of visible categories.</param>
/// <param name="Activity">Σ Activity of visible categories.</param>
/// <param name="Available">Σ Available of visible categories.</param>
/// <param name="Categories">All categories of the group, hidden ones flagged.</param>
/// <param name="IsHidden">Hidden group.</param>
public sealed record BudgetGroupDto(
    Guid Id,
    string Name,
    bool IsSystem,
    Money Assigned,
    Money Activity,
    Money Available,
    IReadOnlyList<BudgetCategoryDto> Categories,
    bool IsHidden);

/// <summary>A category row.</summary>
/// <param name="Id">Category id.</param>
/// <param name="Name">Name.</param>
/// <param name="Assigned">Assigned this month.</param>
/// <param name="Activity">Activity this month.</param>
/// <param name="Available">Available (negative: overspent).</param>
/// <param name="Overspending">Colour of the Available pill.</param>
/// <param name="Target">Target progress, if the category has a target.</param>
/// <param name="Carry">Carried in from last month.</param>
/// <param name="IsHidden">Hidden (itself or via its group).</param>
/// <param name="Kind">Regular or Credit Card Payment.</param>
/// <param name="CardPayment">Card details for a Credit Card Payment category.</param>
public sealed record BudgetCategoryDto(
    Guid Id,
    string Name,
    Money Assigned,
    Money Activity,
    Money Available,
    OverspendingKind Overspending,
    TargetProgressDto? Target,
    Money Carry,
    bool IsHidden,
    BudgetCategoryKind Kind,
    CardPaymentDto? CardPayment);

/// <summary>Card context shown next to a Credit Card Payment category (6.4.5).</summary>
/// <param name="CardAccountId">The credit account.</param>
/// <param name="Covered">Card spending covered by budgeted money this month.</param>
/// <param name="Payments">Payments to the card this month.</param>
/// <param name="CardBalance">Ledger balance of the card at the end of the month (negative = owed).</param>
/// <param name="Difference">Available + CardBalance: negative is owed but not yet covered, positive is surplus.</param>
public sealed record CardPaymentDto(Guid CardAccountId, Money Covered, Money Payments, Money CardBalance, Money Difference);

/// <summary>Progress of a category target in a month.</summary>
/// <param name="Type">Target type.</param>
/// <param name="Amount">Target amount.</param>
/// <param name="Underfunded">"Underfunded by"; zero when funded.</param>
/// <param name="TargetDate">Target date (by-date targets).</param>
/// <param name="NeededThisMonth">Total the target asks to assign this month.</param>
/// <param name="MonthlyNeed">Per-month amount implied by the target.</param>
/// <param name="IsComplete">By-date target reached.</param>
public sealed record TargetProgressDto(
    TargetType Type,
    Money Amount,
    Money Underfunded,
    DateOnly? TargetDate,
    Money NeededThisMonth,
    Money MonthlyNeed,
    bool IsComplete);

/// <summary>A category target (F-BUD-4).</summary>
/// <param name="CategoryId">Category.</param>
/// <param name="Type">Target type.</param>
/// <param name="Amount">Amount in minor units (&gt; 0).</param>
/// <param name="TargetDate">Required for <see cref="TargetType.SavingsBalanceByDate"/>.</param>
/// <param name="LinkedAccountId">Required for <see cref="TargetType.DebtPayment"/>: a liability account.</param>
public sealed record TargetDto(Guid CategoryId, TargetType Type, long Amount, DateOnly? TargetDate = null, Guid? LinkedAccountId = null);

/// <summary>Input for <see cref="IBudgetService.MoveMoneyAsync"/>; a null category means Ready to Assign.</summary>
public sealed record MoveMoneyRequest(DateOnly Month, Guid? FromCategoryId, Guid? ToCategoryId, long Amount);

/// <summary>Result of funding targets.</summary>
/// <param name="Funded">Total newly assigned.</param>
/// <param name="Shortfall">Underfunded amount left because Ready to Assign ran out (0 when all were funded).</param>
/// <param name="CategoriesFunded">Categories that received money.</param>
public sealed record FundTargetsResult(Money Funded, Money Shortfall, int CategoriesFunded)
{
    /// <summary>True when every underfunded target was funded in full.</summary>
    public bool FullyFunded => Shortfall.IsZero;
}

/// <summary>Quick-assign values (F-BUD-5); each is a value to set Assigned to.</summary>
public sealed record QuickAssignDto(
    Guid CategoryId,
    DateOnly Month,
    Money AssignedLastMonth,
    Money SpentLastMonth,
    Money AverageAssigned,
    Money AverageSpent,
    Money? FundTarget,
    Money ResetToZero);

/// <summary>A "How is this computed?" breakdown; the lines with <see cref="ExplanationLineDto.IsTerm"/> sum to <see cref="Total"/>.</summary>
/// <param name="CategoryId">Category, or null for Ready to Assign.</param>
/// <param name="Name">Category name or "Ready to Assign".</param>
/// <param name="Month">Month.</param>
/// <param name="Total">The explained number.</param>
/// <param name="Lines">Lines in reading order.</param>
public sealed record BudgetExplanationDto(Guid? CategoryId, string Name, DateOnly Month, Money Total, IReadOnlyList<ExplanationLineDto> Lines);

/// <summary>One line of a breakdown, with names resolved.</summary>
public sealed record ExplanationLineDto(
    ExplanationLineKind Kind,
    Money Amount,
    bool IsTerm,
    Guid? AccountId,
    string? AccountName,
    Guid? CategoryId,
    string? CategoryName,
    DateOnly? Month);

/// <summary>
/// The ledger-derived part of the budget, loaded once by <see cref="IBudgetService.LoadLedgerAsync"/>
/// and passed back to its overloads. View models treat it as opaque.
/// </summary>
/// <param name="Input">Calculator input without assignments (accounts, groups, categories, activity, card transfers).</param>
/// <param name="CardBalances">Ledger balance of each on-budget credit account at the end of each loaded month.</param>
/// <param name="Currency">Budget currency.</param>
/// <param name="From">First loaded month.</param>
/// <param name="To">Last loaded month.</param>
public sealed record BudgetLedgerData(
    BudgetInput Input,
    IReadOnlyDictionary<DateOnly, Dictionary<Guid, long>> CardBalances,
    string Currency,
    DateOnly From,
    DateOnly To)
{
    /// <summary>Whether <paramref name="month"/> lies in the loaded range.</summary>
    public bool Covers(DateOnly month) => BudgetMonth.Of(month) >= From && BudgetMonth.Of(month) <= To;
}
