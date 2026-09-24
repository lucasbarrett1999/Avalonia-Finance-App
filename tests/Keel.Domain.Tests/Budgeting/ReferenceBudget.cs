using Keel.Domain.Budgeting;

namespace Keel.Domain.Tests.Budgeting;

/// <summary>
/// A deliberately naive, memoized transcription of the PRD 6.4 formulas, used to cross-check
/// <see cref="BudgetCalculator"/> on generated inputs. Slow by design; never used in production.
/// </summary>
internal sealed class ReferenceBudget
{
    private readonly BudgetInput _input;
    private readonly Dictionary<Guid, BudgetAccount> _accounts;
    private readonly Dictionary<Guid, BudgetCategory> _categories;
    private readonly DateOnly _start;
    private readonly Dictionary<(Guid, DateOnly), long> _available = [];
    private readonly Dictionary<(Guid, DateOnly), Dictionary<Guid, long>> _covered = [];

    public ReferenceBudget(BudgetInput input, DateOnly from)
    {
        _input = input;
        _accounts = input.Accounts.ToDictionary(a => a.Id);
        _categories = input.Categories.ToDictionary(c => c.Id);
        var earliest = input.EarliestMonth ?? from;
        _start = earliest < from ? earliest : BudgetMonth.Of(from);
    }

    private IEnumerable<ActivityTotal> OnBudget(DateOnly month) =>
        _input.Activity.Where(a => BudgetMonth.Of(a.Month) == month && _accounts[a.AccountId].IsOnBudget);

    public long Assigned(Guid c, DateOnly month) =>
        _input.Assignments.Where(a => a.CategoryId == c && BudgetMonth.Of(a.Month) == month).Sum(a => a.Assigned);

    public long Activity(Guid c, DateOnly month) => _categories[c].Kind == BudgetCategoryKind.CreditCardPayment
        ? Covered(_categories[c].LinkedAccountId!.Value, month).Values.Sum() - Payments(_categories[c].LinkedAccountId!.Value, month)
        : OnBudget(month).Where(a => a.CategoryId == c).Sum(a => a.Amount);

    public long CardActivity(Guid c, Guid card, DateOnly month) =>
        OnBudget(month).Where(a => a.CategoryId == c && a.AccountId == card).Sum(a => a.Amount);

    private IEnumerable<Guid> Cards => _input.Accounts.Where(a => a.IsBudgetCredit).Select(a => a.Id);

    public long Magnitude(Guid c, DateOnly month) => Cards.Sum(k => Math.Max(0, -CardActivity(c, k, month)));

    public long Payments(Guid card, DateOnly month) => _input.CardTransfers
        .Where(t => t.CardAccountId == card && BudgetMonth.Of(t.Month) == month && _accounts[t.FromAccountId].IsBudgetCash)
        .Sum(t => t.Amount);

    public long Carry(Guid c, DateOnly month) => month <= _start ? 0 : Math.Max(0, Available(c, month.AddMonths(-1)));

    public long Available(Guid c, DateOnly month)
    {
        if (_available.TryGetValue((c, month), out var cached))
        {
            return cached;
        }

        var value = Carry(c, month) + Assigned(c, month) + Activity(c, month);
        _available[(c, month)] = value;
        return value;
    }

    public long CreditOverspent(Guid c, DateOnly month)
    {
        if (_categories[c].Kind == BudgetCategoryKind.CreditCardPayment)
        {
            return 0;
        }

        var overspent = Math.Min(0, Available(c, month));
        return -Math.Min(-overspent, Magnitude(c, month));
    }

    public long CashOverspent(Guid c, DateOnly month) => Math.Min(0, Available(c, month)) - CreditOverspent(c, month);

    /// <summary>Covered(c, K, M) for every spending category c, keyed by c, for card K.</summary>
    public Dictionary<Guid, long> Covered(Guid card, DateOnly month)
    {
        if (_covered.TryGetValue((card, month), out var cached))
        {
            return cached;
        }

        var result = new Dictionary<Guid, long>();
        foreach (var c in _input.Categories.Where(x => x.Kind == BudgetCategoryKind.Regular))
        {
            var byCard = CoveredByCard(c.Id, month);
            if (byCard.TryGetValue(card, out var amount))
            {
                result[c.Id] = amount;
            }
        }

        _covered[(card, month)] = result;
        return result;
    }

    public Dictionary<Guid, long> CoveredByCard(Guid c, DateOnly month)
    {
        var spend = Cards.Select(k => (Card: k, Spend: Math.Max(0, -CardActivity(c, k, month)))).Where(x => x.Spend > 0).ToList();
        var magnitude = spend.Sum(x => x.Spend);
        var uncovered = -CreditOverspent(c, month);
        var shares = spend.ToDictionary(x => x.Card, x => magnitude == 0 ? 0 : (long)Math.Floor((decimal)uncovered * x.Spend / magnitude));
        var remainder = uncovered - shares.Values.Sum();
        var order = spend.OrderByDescending(x => x.Spend).ThenBy(x => _input.Accounts.ToList().FindIndex(a => a.Id == x.Card));
        foreach (var (k, s) in order)
        {
            var take = Math.Min(remainder, s - shares[k]);
            shares[k] += take;
            remainder -= take;
        }

        return spend.ToDictionary(x => x.Card, x => x.Spend - shares[x.Card]);
    }

    public long ReadyToAssign(DateOnly month)
    {
        var inflowIds = _input.Categories.Where(c => c.Kind == BudgetCategoryKind.Inflow).Select(c => c.Id).ToHashSet();
        long inflow = _input.Activity
            .Where(a => a.CategoryId is { } id && inflowIds.Contains(id) && _accounts[a.AccountId].IsOnBudget && BudgetMonth.Of(a.Month) <= month)
            .Sum(a => a.Amount);
        long assigned = _input.Assignments.Where(a => !inflowIds.Contains(a.CategoryId)).Sum(a => a.Assigned);
        long overspent = 0;
        for (var m = _start; m < month; m = m.AddMonths(1))
        {
            overspent += _input.Categories.Where(c => c.Kind != BudgetCategoryKind.Inflow).Sum(c => CashOverspent(c.Id, m));
        }

        return inflow - assigned + overspent;
    }
}
