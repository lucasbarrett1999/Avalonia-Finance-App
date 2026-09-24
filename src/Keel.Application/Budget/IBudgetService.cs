using Keel.Domain;

namespace Keel.Application.Budget;

/// <summary>Envelope-budget use cases (F-BUD-*). Implemented in M2 on top of the BudgetCalculator.</summary>
public interface IBudgetService
{
    /// <summary>Computes the budget grid for a month (first-of-month date).</summary>
    Task<BudgetMonthDto> GetMonthAsync(DateOnly month, CancellationToken ct);

    /// <summary>Sets Assigned for a category and month (absent row means 0).</summary>
    Task AssignAsync(Guid categoryId, DateOnly month, long assigned, CancellationToken ct);

    /// <summary>Moves Available between categories or to/from Ready to Assign (F-BUD-3).</summary>
    Task MoveMoneyAsync(MoveMoneyRequest request, CancellationToken ct);

    /// <summary>Assigns what every underfunded target needs, limited by Ready to Assign (F-BUD-4).</summary>
    Task<FundTargetsResult> FundTargetsAsync(DateOnly month, CancellationToken ct);
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
public sealed record BudgetMonthDto(
    DateOnly Month,
    Money ReadyToAssign,
    Money TotalAssigned,
    Money TotalActivity,
    Money TotalAvailable,
    IReadOnlyList<BudgetGroupDto> Groups);

/// <summary>A category group row with totals of its visible categories.</summary>
public sealed record BudgetGroupDto(
    Guid Id,
    string Name,
    bool IsSystem,
    Money Assigned,
    Money Activity,
    Money Available,
    IReadOnlyList<BudgetCategoryDto> Categories);

/// <summary>A category row.</summary>
public sealed record BudgetCategoryDto(
    Guid Id,
    string Name,
    Money Assigned,
    Money Activity,
    Money Available,
    OverspendingKind Overspending,
    TargetProgressDto? Target);

/// <summary>Progress of a category target in a month.</summary>
public sealed record TargetProgressDto(TargetType Type, Money Amount, Money Underfunded, DateOnly? TargetDate);

/// <summary>Input for <see cref="IBudgetService.MoveMoneyAsync"/>; a null category means Ready to Assign.</summary>
public sealed record MoveMoneyRequest(DateOnly Month, Guid? FromCategoryId, Guid? ToCategoryId, long Amount);

/// <summary>Result of funding targets.</summary>
public sealed record FundTargetsResult(Money Funded, Money Shortfall, int CategoriesFunded);
