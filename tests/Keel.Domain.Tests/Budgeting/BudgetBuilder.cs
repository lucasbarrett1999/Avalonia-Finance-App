using System.Globalization;
using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Domain.Entities;

namespace Keel.Domain.Tests.Budgeting;

/// <summary>
/// Builds <see cref="BudgetInput"/> from ledger-like rows, aggregating them the way the SQL
/// aggregation does: categorized rows by (category, month, account); uncategorized non-transfer rows
/// with a null category; uncategorized transfer rows are not activity, and the positive card side
/// of such a transfer is a card transfer.
/// </summary>
internal sealed class BudgetBuilder
{
    public static readonly Guid EverydayGroup = new("00000000-0000-7000-8000-0000000000a0");

    private readonly List<BudgetAccount> _accounts = [];
    private readonly List<BudgetGroup> _groups =
    [
        new(SystemIds.InflowGroup, "Inflow", 0, IsSystem: true),
        new(SystemIds.CreditCardPaymentsGroup, "Credit Card Payments", 1, IsSystem: true),
        new(EverydayGroup, "Everyday", 2),
    ];

    private readonly List<BudgetCategory> _categories =
    [
        new(SystemIds.ReadyToAssignCategory, SystemIds.InflowGroup, "Ready to Assign", 0, BudgetCategoryKind.Inflow),
    ];

    private readonly List<Row> _rows = [];
    private readonly List<AssignmentTotal> _assignments = [];
    private readonly Dictionary<Guid, Guid> _paymentCategories = [];
    private int _ids;

    public Guid Rta => SystemIds.ReadyToAssignCategory;

    public IReadOnlyList<BudgetAccount> Accounts => _accounts;

    public IReadOnlyList<BudgetCategory> Categories => _categories;

    public IReadOnlyList<BudgetGroup> Groups => _groups;

    public static DateOnly D(string isoDate) => DateOnly.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly M(string isoMonth) => DateOnly.ParseExact(isoMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private Guid NextId() => new($"00000000-0000-7000-8000-{++_ids:x12}");

    public Guid Account(string name, AccountType type, bool? onBudget = null, bool closed = false)
    {
        var id = NextId();
        _accounts.Add(new BudgetAccount(id, name, type, onBudget ?? AccountTypeInfo.IsOnBudgetByDefault(type), closed));
        if ((onBudget ?? AccountTypeInfo.IsOnBudgetByDefault(type)) && AccountTypeInfo.IsCredit(type))
        {
            var pay = NextId();
            _categories.Add(new BudgetCategory(pay, SystemIds.CreditCardPaymentsGroup, "Pay_" + name, _paymentCategories.Count, BudgetCategoryKind.CreditCardPayment, id));
            _paymentCategories[id] = pay;
        }

        return id;
    }

    public Guid PaymentCategory(Guid card) => _paymentCategories[card];

    public Guid Group(string name, bool hidden = false)
    {
        var id = NextId();
        _groups.Add(new BudgetGroup(id, name, _groups.Count, IsHidden: hidden));
        return id;
    }

    public Guid Category(string name, Guid? group = null, bool hidden = false)
    {
        var id = NextId();
        _categories.Add(new BudgetCategory(id, group ?? EverydayGroup, name, _categories.Count, IsHidden: hidden));
        return id;
    }

    public void Hide(Guid category)
    {
        var index = _categories.FindIndex(c => c.Id == category);
        _categories[index] = _categories[index] with { IsHidden = true };
    }

    /// <summary>A plain transaction; a null category means uncategorized.</summary>
    public BudgetBuilder Txn(string date, Guid account, long amount, Guid? category)
    {
        _rows.Add(new Row(D(date), account, amount, category, null));
        return this;
    }

    /// <summary>A split transaction; each split counts individually and the parent contributes nothing.</summary>
    public BudgetBuilder Split(string date, Guid account, params (Guid? Category, long Amount)[] splits)
    {
        foreach (var (category, amount) in splits)
        {
            _rows.Add(new Row(D(date), account, amount, category, null));
        }

        return this;
    }

    /// <summary>
    /// A transfer of <paramref name="amount"/> (&gt; 0) from <paramref name="from"/> to <paramref name="to"/>:
    /// two rows, −amount on the source and +amount on the destination, each with its own category
    /// (on-budget ↔ on-budget transfers normally have none).
    /// </summary>
    public BudgetBuilder Transfer(string date, Guid from, Guid to, long amount, Guid? fromCategory = null, Guid? toCategory = null)
    {
        _rows.Add(new Row(D(date), from, -amount, fromCategory, to));
        _rows.Add(new Row(D(date), to, amount, toCategory, from));
        return this;
    }

    public BudgetBuilder Assign(Guid category, string month, long amount)
    {
        _assignments.Add(new AssignmentTotal(category, M(month), amount));
        return this;
    }

    public BudgetInput Build()
    {
        var activity = _rows
            .Where(r => r.Category is not null || r.TransferAccount is null)
            .GroupBy(r => (r.Category, Month: BudgetMonth.Of(r.Date), r.Account))
            .Select(g => new ActivityTotal(g.Key.Category, g.Key.Month, g.Key.Account, g.Sum(r => r.Amount)))
            .ToList();

        var credit = _accounts.Where(a => AccountTypeInfo.IsCredit(a.Type)).Select(a => a.Id).ToHashSet();
        var transfers = _rows
            .Where(r => r.Category is null && r.TransferAccount is not null && r.Amount > 0 && credit.Contains(r.Account))
            .GroupBy(r => (r.Account, From: r.TransferAccount!.Value, Month: BudgetMonth.Of(r.Date)))
            .Select(g => new CardTransferTotal(g.Key.Account, g.Key.From, g.Key.Month, g.Sum(r => r.Amount)))
            .ToList();

        return new BudgetInput(_accounts.ToList(), _groups.ToList(), _categories.ToList(), activity, _assignments.ToList(), transfers);
    }

    public BudgetSnapshot Compute(string fromMonth, string toMonth) => BudgetCalculator.Compute(Build(), M(fromMonth), M(toMonth));

    public string NameOf(Guid id) =>
        _accounts.FirstOrDefault(a => a.Id == id)?.Name
        ?? _categories.FirstOrDefault(c => c.Id == id)?.Name
        ?? _groups.FirstOrDefault(g => g.Id == id)?.Name
        ?? id.ToString();

    private sealed record Row(DateOnly Date, Guid Account, long Amount, Guid? Category, Guid? TransferAccount);
}
