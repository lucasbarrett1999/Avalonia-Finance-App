using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Infrastructure.Budgeting;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Keel.Infrastructure.Tests.Budgeting.TestLedger;

namespace Keel.Infrastructure.Tests.Budgeting;

public sealed class BudgetAggregationQueryTests : IDisposable
{
    private TestLedger _ledger = null!;

    public void Dispose() => _ledger?.Dispose();

    /// <summary>
    /// A hand-built ledger that exercises every aggregation rule, and the aggregated rows it must
    /// produce (the calculator input written by hand).
    /// </summary>
    internal sealed record HandLedger(
        TestLedger Ledger,
        Guid Checking,
        Guid Savings,
        Guid Visa,
        Guid Brokerage,
        Guid OldVisa,
        Guid Groceries,
        Guid Household,
        Guid Dining,
        Guid Retirement,
        IReadOnlyList<ActivityTotal> ExpectedActivity,
        IReadOnlyList<CardTransferTotal> ExpectedTransfers);

    internal static async Task<HandLedger> BuildAsync()
    {
        var l = await CreateAsync();
        var checking = l.Account("Checking", AccountType.Checking);
        var savings = l.Account("Savings", AccountType.Savings);
        var visa = l.Account("Visa", AccountType.CreditCard);
        var brokerage = l.Account("Brokerage", AccountType.Investment);
        var oldVisa = l.Account("Old Visa", AccountType.CreditCard, closed: true);
        var groceries = l.Category("Groceries");
        var household = l.Category("Household");
        var dining = l.Category("Dining");
        var retirement = l.Category("Retirement");

        l.Txn("2026-08-01", checking, 3_000_00, Rta);
        l.Txn("2026-08-05", checking, -100_00, groceries);
        l.Txn("2026-08-31", checking, -40_00, groceries);                  // last day of August
        l.Txn("2026-09-01", checking, -10_00, groceries);                  // first day of September
        l.Txn("2026-08-10", visa, -250_00, groceries);
        l.Txn("2026-08-12", checking, -25_00, null);                       // uncategorized, not a transfer
        l.Transfer("2026-08-15", checking, savings, 500_00);               // on budget ↔ on budget: nothing
        l.Transfer("2026-08-25", checking, visa, 100_00);                  // card payment
        l.Transfer("2026-08-26", checking, brokerage, 300_00, fromCategory: retirement); // to tracking: categorized outflow
        l.Split("2026-08-20", checking, false, (groceries, null, -60_00), (household, null, -30_00));
        l.Split("2026-08-21", visa, false, (null, checking, 30_00), (dining, null, -20_00)); // split card payment + purchase
        l.Txn("2026-08-21", checking, -30_00, null).TransferAccountId = visa; // checking side of that split payment
        l.Txn("2026-08-22", checking, -999_00, groceries, deleted: true);  // soft-deleted
        l.Split("2026-08-23", checking, true, (groceries, null, -500_00), (dining, null, -500_00)); // soft-deleted split
        l.Txn("2026-08-24", brokerage, 75_00, Rta);                        // tracking rows never count
        l.Txn("2026-06-10", oldVisa, -80_00, dining);                      // closed account, history counts
        l.Txn("2028-02-29", checking, -5_00, dining);                      // leap day
        l.Txn("2028-03-01", checking, -7_00, dining);
        l.Transfer("2026-08-27", visa, checking, 50_00);                   // uncategorized cash advance: nothing
        l.Assignment(groceries, "2026-08", 400_00);
        l.Assignment(dining, "2026-09", 50_00);
        await l.SaveAsync();

        ActivityTotal A(Guid? c, string month, Guid account, long amount) => new(c, M(month), account, amount);
        return new HandLedger(l, checking, savings, visa, brokerage, oldVisa, groceries, household, dining, retirement,
            [
                A(Rta, "2026-08", checking, 3_000_00),
                A(groceries, "2026-08", checking, -200_00),
                A(groceries, "2026-09", checking, -10_00),
                A(groceries, "2026-08", visa, -250_00),
                A(null, "2026-08", checking, -25_00),
                A(retirement, "2026-08", checking, -300_00),
                A(household, "2026-08", checking, -30_00),
                A(dining, "2026-08", visa, -20_00),
                A(dining, "2026-06", oldVisa, -80_00),
                A(dining, "2028-02", checking, -5_00),
                A(dining, "2028-03", checking, -7_00),
            ],
            [new CardTransferTotal(visa, checking, M("2026-08"), 130_00)]);
    }

    private static List<ActivityTotal> Normalize(IEnumerable<ActivityTotal> rows) =>
        [.. rows.GroupBy(r => (r.CategoryId, r.Month, r.AccountId))
            .Select(g => new ActivityTotal(g.Key.CategoryId, g.Key.Month, g.Key.AccountId, g.Sum(r => r.Amount)))
            .OrderBy(r => r.Month).ThenBy(r => r.AccountId).ThenBy(r => r.CategoryId)];

    [Fact]
    public async Task Aggregates_activity_by_category_month_and_account()
    {
        var hand = await BuildAsync();
        _ledger = hand.Ledger;
        await using var db = _ledger.Factory.CreateDbContext();

        var activity = await BudgetAggregationQuery.ActivityAsync(db, CancellationToken.None);

        Normalize(activity).ShouldBe(Normalize(hand.ExpectedActivity));
    }

    [Fact]
    public async Task Aggregates_payments_into_credit_accounts_including_split_rows()
    {
        var hand = await BuildAsync();
        _ledger = hand.Ledger;
        await using var db = _ledger.Factory.CreateDbContext();

        var transfers = await BudgetAggregationQuery.CardTransfersAsync(db, CancellationToken.None);

        transfers.GroupBy(t => (t.CardAccountId, t.FromAccountId, t.Month))
            .Select(g => new CardTransferTotal(g.Key.CardAccountId, g.Key.FromAccountId, g.Key.Month, g.Sum(t => t.Amount)))
            .ShouldBe(hand.ExpectedTransfers);
    }

    [Fact]
    public async Task Loads_accounts_categories_and_assignments()
    {
        var hand = await BuildAsync();
        _ledger = hand.Ledger;
        await using var db = _ledger.Factory.CreateDbContext();

        var input = await BudgetAggregationQuery.LoadInputAsync(db, CancellationToken.None);

        input.Accounts.Count.ShouldBe(5);
        input.Accounts.Single(a => a.Id == hand.OldVisa).IsClosed.ShouldBeTrue();
        input.Accounts.Single(a => a.Id == hand.Brokerage).IsOnBudget.ShouldBeFalse();
        input.Categories.Single(c => c.Id == Rta).Kind.ShouldBe(BudgetCategoryKind.Inflow);
        input.Categories.Single(c => c.Id == _ledger.PaymentCategories[hand.Visa]).Kind.ShouldBe(BudgetCategoryKind.CreditCardPayment);
        input.Categories.Single(c => c.Id == hand.Groceries).Kind.ShouldBe(BudgetCategoryKind.Regular);
        input.Assignments.ShouldBe([new AssignmentTotal(hand.Groceries, M("2026-08"), 400_00), new AssignmentTotal(hand.Dining, M("2026-09"), 50_00)], ignoreOrder: true);
        input.Groups.Select(g => g.Name).ShouldBe(["Inflow", "Credit Card Payments", "Everyday"], ignoreOrder: true);
    }

    [Fact]
    public async Task Card_balances_are_cumulative_per_month()
    {
        var hand = await BuildAsync();
        _ledger = hand.Ledger;
        await using var db = _ledger.Factory.CreateDbContext();

        var balances = await BudgetAggregationQuery.CardBalancesAsync(db, M("2026-07"), M("2026-09"), CancellationToken.None);

        balances[M("2026-07")].ShouldNotContainKey(hand.Visa);
        balances[M("2026-07")][hand.OldVisa].ShouldBe(-80_00);
        balances[M("2026-08")][hand.Visa].ShouldBe(-250_00 + 100_00 + 10_00 - 50_00);
        balances[M("2026-09")][hand.Visa].ShouldBe(-190_00);
        balances.Keys.ShouldBe([M("2026-07"), M("2026-08"), M("2026-09")]);
    }

    [Fact]
    public async Task Aggregation_runs_in_sql_with_group_by_and_never_loads_transaction_rows()
    {
        var hand = await BuildAsync();
        _ledger = hand.Ledger;
        var commands = new List<string>();
        var options = new DbContextOptionsBuilder<KeelDbContext>()
            .UseSqlite(KeelDatabase.ConnectionString(_ledger.Factory.CurrentPath!))
            .AddInterceptors(SqlitePragmaInterceptor.Instance)
            .LogTo(commands.Add, [DbLoggerCategory.Database.Command.Name], LogLevel.Information)
            .Options;
        await using var db = new KeelDbContext(options);

        await BudgetAggregationQuery.LoadInputAsync(db, CancellationToken.None);

        var ledgerQueries = commands.Where(c => c.Contains("\"Transactions\"", StringComparison.Ordinal)).ToList();
        ledgerQueries.Count.ShouldBe(4);   // parent activity, split activity, parent payments, split payments
        foreach (var sql in ledgerQueries)
        {
            sql.ShouldContain("GROUP BY");
            sql.ShouldContain("SUM(");
            sql.ShouldNotContain("PayeeRaw");                                  // no row projection
            sql.ShouldContain("substr(t.\"Date\", 1, 7)");                     // month computed in SQL
        }

        ledgerQueries.ShouldAllBe(sql => sql.Contains("t.\"IsDeleted\" = 0", StringComparison.Ordinal)); // soft delete applied
    }

    [Fact]
    public void Activity_scans_the_table_instead_of_the_category_index()
    {
        // The plan choice is the point of the raw SQL (see BudgetAggregationQuery remarks).
        BudgetAggregationQuery.ParentActivitySql.ShouldContain("NOT INDEXED");
        BudgetAggregationQuery.ParentCardTransferSql.ShouldContain("IN('CreditCard', 'LineOfCredit')");
    }
}
