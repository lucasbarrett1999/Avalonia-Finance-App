using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Domain.Entities;

namespace Keel.Application.Budget;

/// <summary>Maps calculator results to the DTOs view models consume (no entities leave the service).</summary>
public static class BudgetDtoMapper
{
    /// <summary>The name shown for Ready to Assign in explanations.</summary>
    public const string ReadyToAssignName = "Ready to Assign";

    /// <summary>Maps one month.</summary>
    /// <param name="month">Calculator result.</param>
    /// <param name="input">The input it was computed from (names, groups).</param>
    /// <param name="targets">Targets by category.</param>
    /// <param name="cardBalances">Ledger balance of each credit account at the end of the month.</param>
    /// <param name="currency">Budget currency.</param>
    public static BudgetMonthDto ToDto(
        BudgetMonthResult month,
        BudgetInput input,
        IReadOnlyDictionary<Guid, Target> targets,
        IReadOnlyDictionary<Guid, long> cardBalances,
        string currency)
    {
        ArgumentNullException.ThrowIfNull(month);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(cardBalances);

        Money M(long amount) => new(amount, currency);
        var categories = input.Categories.ToDictionary(c => c.Id);
        var groups = input.Groups.ToDictionary(g => g.Id);
        var cells = month.Categories.ToDictionary(c => c.CategoryId);

        var groupDtos = month.Groups.Select(g =>
        {
            var group = groups[g.GroupId];
            var rows = g.CategoryIds.Select(id =>
            {
                var cell = cells[id];
                TargetProgressDto? progress = null;
                if (targets.TryGetValue(id, out var target))
                {
                    var status = TargetCalculator.Compute(target, cell);
                    progress = new TargetProgressDto(status.Type, M(status.Amount), M(status.Underfunded), status.TargetDate, M(status.NeededThisMonth), M(status.MonthlyNeed), status.IsComplete);
                }

                CardPaymentDto? card = null;
                if (cell.CardAccountId is { } cardId)
                {
                    var balance = cardBalances.TryGetValue(cardId, out var b) ? b : 0;
                    card = new CardPaymentDto(cardId, M(cell.Covered), M(cell.Payments), M(balance), M(cell.Available + balance));
                }

                return new BudgetCategoryDto(
                    id,
                    categories[id].Name,
                    M(cell.Assigned),
                    M(cell.Activity),
                    M(cell.Available),
                    ToKind(cell.Overspending),
                    progress,
                    M(cell.Carry),
                    !cell.IsVisible,
                    cell.Kind,
                    card);
            }).ToList();

            return new BudgetGroupDto(group.Id, group.Name, group.IsSystem, M(g.Assigned), M(g.Activity), M(g.Available), rows, g.IsHidden);
        }).ToList();

        return new BudgetMonthDto(
            month.Month,
            M(month.ReadyToAssign),
            M(month.TotalAssigned),
            M(month.TotalActivity),
            M(month.TotalAvailable),
            groupDtos,
            M(month.AssignedInFuture),
            M(month.UncategorizedActivity));
    }

    /// <summary>Maps an explanation, resolving account and category names.</summary>
    public static BudgetExplanationDto ToDto(BudgetExplanation explanation, BudgetInput input, string currency)
    {
        ArgumentNullException.ThrowIfNull(explanation);
        ArgumentNullException.ThrowIfNull(input);
        var accounts = input.Accounts.ToDictionary(a => a.Id, a => a.Name);
        var categories = input.Categories.ToDictionary(c => c.Id, c => c.Name);
        string? Name(Dictionary<Guid, string> names, Guid? id) => id is { } key && names.TryGetValue(key, out var n) ? n : null;

        var lines = explanation.Lines.Select(l => new ExplanationLineDto(
            l.Kind,
            new Money(l.Amount, currency),
            l.IsTerm,
            l.AccountId,
            Name(accounts, l.AccountId),
            l.CategoryId,
            Name(categories, l.CategoryId),
            l.Month)).ToList();

        var title = explanation.CategoryId is { } id ? categories[id] : ReadyToAssignName;
        return new BudgetExplanationDto(explanation.CategoryId, title, explanation.Month, new Money(explanation.Total, currency), lines);
    }

    /// <summary>Maps quick-assign values.</summary>
    public static QuickAssignDto ToDto(QuickAssignValues values, Guid categoryId, DateOnly month, string currency)
    {
        ArgumentNullException.ThrowIfNull(values);
        Money M(long amount) => new(amount, currency);
        return new QuickAssignDto(
            categoryId,
            month,
            M(values.AssignedLastMonth),
            M(values.SpentLastMonth),
            M(values.AverageAssigned),
            M(values.AverageSpent),
            values.FundTarget is { } fund ? M(fund) : null,
            M(values.ResetToZero));
    }

    /// <summary>Maps the domain overspending state to the DTO enum.</summary>
    public static OverspendingKind ToKind(BudgetOverspending overspending) => overspending switch
    {
        BudgetOverspending.Cash => OverspendingKind.Cash,
        BudgetOverspending.Credit => OverspendingKind.Credit,
        _ => OverspendingKind.None,
    };
}
