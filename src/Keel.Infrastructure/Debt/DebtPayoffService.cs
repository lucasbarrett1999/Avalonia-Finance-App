using System.Globalization;
using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Categories;
using Keel.Application.Debt;
using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Domain.Debt;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Debt;

/// <summary>
/// <see cref="IDebtPayoffService"/> (F-GOAL-2, ADR 0093): open liability accounts that are owed
/// something are the debts; balances follow the net-worth rule (ledger, or the latest snapshot plus later
/// activity for a tracking account with snapshots, ADR 0060); the math is
/// <see cref="DebtPayoffCalculator"/>'s; targets are written through <see cref="IBudgetService.SetTargetsAsync"/>.
/// </summary>
public sealed class DebtPayoffService(IDbContextFactory<KeelDbContext> factory, IBudgetService budget, ICategoryService categories, TimeProvider time) : IDebtPayoffService
{
    /// <inheritdoc />
    public Task<DebtPayoffOverview> GetPlanAsync(long extraPerMonth, DebtOrdering ordering, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(extraPerMonth);
        return Task.Run(() => PlanAsync(extraPerMonth, ordering, ct), ct);
    }

    /// <inheritdoc />
    public async Task<DebtTargetsResult> SetPaymentTargetsAsync(long extraPerMonth, DebtOrdering ordering, NewPaymentCategory names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        var overview = await GetPlanAsync(extraPerMonth, ordering, ct).ConfigureAwait(false);
        var existing = await categories.GetCategoriesAsync(includeHidden: true, ct).ConfigureAwait(false);
        var targets = new List<TargetDto>();
        var created = 0;
        foreach (var schedule in overview.Plan.Debts.Where(s => s.FirstPayment > 0))
        {
            var debt = overview.Debts.Single(d => d.AccountId == schedule.Id);
            var categoryId = debt.PaymentCategoryId;
            if (categoryId is null)
            {
                // A loan without a payment category: reuse "{name} payment" in the group, or create it.
                var name = string.Format(CultureInfo.CurrentCulture, names.NameFormat, debt.Name);
                categoryId = existing.FirstOrDefault(c =>
                    string.Equals(c.GroupName, names.GroupName, StringComparison.CurrentCultureIgnoreCase)
                    && string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase))?.Id;
                if (categoryId is null)
                {
                    categoryId = (await categories.CreateCategoryAsync(names.GroupName, name, ct).ConfigureAwait(false)).Id;
                    created++;
                }
            }

            if (targets.All(t => t.CategoryId != categoryId))
            {
                targets.Add(new TargetDto(categoryId.Value, TargetType.DebtPayment, schedule.FirstPayment, null, debt.AccountId));
            }
        }

        if (targets.Count > 0)
        {
            await budget.SetTargetsAsync(targets, ct).ConfigureAwait(false);
        }

        return new DebtTargetsResult(targets.Count, created);
    }

    private async Task<DebtPayoffOverview> PlanAsync(long extraPerMonth, DebtOrdering ordering, CancellationToken ct)
    {
        var db = factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var accounts = await db.Accounts.AsNoTracking().Where(a => !a.IsClosed).ToListAsync(ct).ConfigureAwait(false);
            var currency = accounts.Where(a => a.IsOnBudget).OrderBy(a => a.SortOrder).ThenBy(a => a.Id).Select(a => a.Currency).FirstOrDefault() ?? Currency.Default;
            var liabilities = accounts.Where(a => DebtTerms.AppliesTo(a.Type)).OrderBy(a => a.Group).ThenBy(a => a.SortOrder).ThenBy(a => a.Id).ToList();
            var ids = liabilities.Select(a => a.Id).ToList();

            var ledger = await db.Transactions.Where(t => ids.Contains(t.AccountId))
                .GroupBy(t => t.AccountId)
                .Select(g => new { g.Key, Sum = g.Sum(t => t.Amount) })
                .ToDictionaryAsync(x => x.Key, x => x.Sum, ct).ConfigureAwait(false);
            var snapshots = (await db.BalanceSnapshots.AsNoTracking().Where(s => ids.Contains(s.AccountId)).ToListAsync(ct).ConfigureAwait(false))
                .GroupBy(s => s.AccountId)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Date).Last());

            var balances = new Dictionary<Guid, long>();
            foreach (var account in liabilities)
            {
                if (!account.IsOnBudget && snapshots.TryGetValue(account.Id, out var snapshot))
                {
                    var after = await db.Transactions.Where(t => t.AccountId == account.Id && t.Date > snapshot.Date)
                        .SumAsync(t => t.Amount, ct).ConfigureAwait(false);
                    balances[account.Id] = snapshot.Balance + after;
                }
                else
                {
                    balances[account.Id] = ledger.GetValueOrDefault(account.Id);
                }
            }

            var owed = liabilities.Where(a => balances[a.Id] < 0).ToList();
            var owedIds = owed.Select(a => a.Id).ToList();
            var categoryNames = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct).ConfigureAwait(false);
            var cardCategories = await db.Categories.AsNoTracking()
                .Where(c => c.LinkedAccountId != null && owedIds.Contains(c.LinkedAccountId.Value))
                .ToDictionaryAsync(c => c.LinkedAccountId!.Value, c => c.Id, ct).ConfigureAwait(false);
            var debtTargets = (await db.Targets.AsNoTracking()
                    .Where(t => t.Type == TargetType.DebtPayment && t.LinkedAccountId != null && owedIds.Contains(t.LinkedAccountId.Value))
                    .ToListAsync(ct).ConfigureAwait(false))
                .OrderBy(t => t.CategoryId)
                .ToList();

            // The category used most often on categorized transfers into the debt (the on-budget side, PRD 6.3).
            var transferUse = await db.Transactions.AsNoTracking()
                .Where(t => t.TransferAccountId != null && owedIds.Contains(t.TransferAccountId.Value) && t.CategoryId != null)
                .GroupBy(t => new { Account = t.TransferAccountId!.Value, Category = t.CategoryId!.Value })
                .Select(g => new { g.Key.Account, g.Key.Category, Count = g.Count() })
                .ToListAsync(ct).ConfigureAwait(false);
            var splitUse = await db.TransactionSplits.AsNoTracking()
                .Where(s => s.TransferAccountId != null && owedIds.Contains(s.TransferAccountId.Value) && s.CategoryId != null)
                .GroupBy(s => new { Account = s.TransferAccountId!.Value, Category = s.CategoryId!.Value })
                .Select(g => new { g.Key.Account, g.Key.Category, Count = g.Count() })
                .ToListAsync(ct).ConfigureAwait(false);
            var mostUsed = transferUse.Concat(splitUse)
                .GroupBy(x => x.Account)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(x => x.Category).Select(c => (Category: c.Key, Count: c.Sum(x => x.Count)))
                        .OrderByDescending(c => c.Count).ThenBy(c => categoryNames.GetValueOrDefault(c.Category), StringComparer.CurrentCultureIgnoreCase).First().Category);

            var dtos = owed.Select(a =>
            {
                Guid? category = cardCategories.TryGetValue(a.Id, out var card) ? card
                    : debtTargets.FirstOrDefault(t => t.LinkedAccountId == a.Id)?.CategoryId
                    ?? (mostUsed.TryGetValue(a.Id, out var used) ? used : null);
                var target = category is { } c ? debtTargets.FirstOrDefault(t => t.CategoryId == c && t.LinkedAccountId == a.Id) : null;
                return new DebtAccountDto(
                    a.Id,
                    a.Name,
                    a.Type,
                    a.IsOnBudget,
                    -balances[a.Id],
                    a.InterestRateBps,
                    a.MinimumPayment,
                    category,
                    category is { } id ? categoryNames.GetValueOrDefault(id) : null,
                    target?.Amount);
            }).ToList();

            var planned = dtos.Where(d => d.HasTerms).ToList();
            var plan = DebtPayoffCalculator.Plan(planned.Select(d => d.ToInput()).ToList(), extraPerMonth, ordering);
            var order = plan.Debts.Select(s => planned.Single(d => d.AccountId == s.Id)).ToList();
            var minimum = DebtPayoffCalculator.MinimumOnly(order.Select(d => d.ToInput()).ToList());
            var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
            return new DebtPayoffOverview(currency, BudgetMonth.Of(today), order, [.. dtos.Where(d => !d.HasTerms)], plan, minimum);
        }
    }
}
