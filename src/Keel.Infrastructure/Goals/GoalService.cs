using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Categories;
using Keel.Application.Goals;
using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Domain.Reports;

namespace Keel.Infrastructure.Goals;

/// <summary>
/// <see cref="IGoalService"/> over the existing budget, category and account services: goals are
/// categories with a savings-balance-by-date target (F-GOAL-1), so every number comes from
/// <see cref="IBudgetService"/> (and therefore from <see cref="BudgetCalculator"/>).
/// </summary>
public sealed class GoalService(IBudgetService budget, ICategoryService categories, IAccountService accounts) : IGoalService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<GoalDto>> GetGoalsAsync(DateOnly month, CancellationToken ct)
    {
        month = BudgetMonth.Of(month);
        var range = await budget.GetRangeAsync(month.AddMonths(1 - GoalProjection.PaceMonths), month, ct).ConfigureAwait(false);
        var current = range[^1];
        var goals = current.Groups
            .Where(g => !g.IsHidden)
            .SelectMany(g => g.Categories.Where(c => !c.IsHidden && c.Target is { Type: TargetType.SavingsBalanceByDate }).Select(c => (Group: g, Category: c)))
            .ToList();
        if (goals.Count == 0)
        {
            return [];
        }

        var accountList = await accounts.GetAccountsAsync(includeClosed: true, ct).ConfigureAwait(false);
        var result = new List<GoalDto>(goals.Count);
        foreach (var (group, category) in goals)
        {
            var target = await budget.GetTargetAsync(category.Id, ct).ConfigureAwait(false);
            var assigned = range
                .Select(m => m.Groups.SelectMany(g => g.Categories).FirstOrDefault(c => c.Id == category.Id)?.Assigned.Amount ?? 0)
                .ToList();
            var linked = target?.LinkedAccountId is { } linkedId ? accountList.FirstOrDefault(a => a.Id == linkedId) : null;
            var progress = category.Target!;
            result.Add(new GoalDto(
                category.Id,
                category.Name,
                group.Name,
                current.ReadyToAssign.Currency,
                progress.Amount.Amount,
                progress.TargetDate ?? month,
                category.Available.Amount,
                category.Assigned.Amount,
                progress.MonthlyNeed.Amount,
                progress.Underfunded.Amount,
                GoalProjection.AveragePace(assigned),
                linked?.Id,
                linked?.Name,
                linked?.Balance.Amount));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<GoalDto> CreateGoalAsync(CreateGoalRequest request, DateOnly month, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Amount, "A goal amount must be positive.");
        }

        if (request.LinkedAccountId is { } linked
            && await accounts.GetAccountAsync(linked, ct).ConfigureAwait(false) is not { IsOnBudget: false })
        {
            throw new ArgumentException("A goal can only be linked to a tracking account.", nameof(request));
        }

        var category = await categories.CreateCategoryAsync(request.GroupName, request.Name, ct).ConfigureAwait(false);
        await budget.SetTargetAsync(
            new TargetDto(category.Id, TargetType.SavingsBalanceByDate, request.Amount, request.TargetDate, request.LinkedAccountId),
            ct).ConfigureAwait(false);
        var goals = await GetGoalsAsync(month, ct).ConfigureAwait(false);
        return goals.Single(g => g.CategoryId == category.Id);
    }
}
