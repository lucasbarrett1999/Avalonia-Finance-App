using System.Diagnostics;
using Keel.Application.Budget;
using Keel.Application.Reports;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Reports;
using Keel.Infrastructure.Tests.Budgeting;
using Keel.Infrastructure.Tests.Ledger;
using Xunit.Abstractions;
using static Keel.Infrastructure.Tests.Budgeting.TestLedger;

namespace Keel.Infrastructure.Tests.Reports;

public sealed class ReportServiceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private TestLedger? _ledger;

    public void Dispose() => _ledger?.Dispose();

    private sealed record Ledger(
        TestLedger L,
        ReportService Service,
        Guid Checking,
        Guid Visa,
        Guid Brokerage,
        Guid Mortgage,
        Guid Groceries,
        Guid Dining,
        Guid Rent,
        Guid Investing,
        Guid Interest,
        Guid Bills);

    // A hand-built ledger (TestLedger's clock is 2026-08-15): July and August activity in two
    // on-budget accounts and two tracking accounts, with splits, a deleted row, system rows,
    // transfers (uncategorized card payment, categorized transfer to tracking) and snapshots.
    private async Task<Ledger> BuildAsync()
    {
        var l = _ledger = await CreateAsync();
        var bills = EntityIds.New();
        await using (var db = l.Factory.CreateDbContext())
        {
            db.CategoryGroups.Add(new CategoryGroup { Id = bills, Name = "Bills", SortOrder = 3 });
            await db.SaveChangesAsync();
        }

        var checking = l.Account("Checking", AccountType.Checking);
        var visa = l.Account("Visa", AccountType.CreditCard);
        var brokerage = l.Account("Brokerage", AccountType.Investment);
        var mortgage = l.Account("Mortgage", AccountType.Loan);
        var groceries = l.Category("Groceries");
        var dining = l.Category("Dining");
        var investing = l.Category("Investing");
        var rent = l.Category("Rent", bills);
        var interest = l.Category("Interest", bills);

        // Opening balances are system rows: never income or spending, always balance.
        l.Txn("2026-06-01", checking, 1_000_00, Rta).Source = TransactionSource.System;
        l.Txn("2026-06-01", mortgage, -100_000_00, null).Source = TransactionSource.System;
        l.Txn("2026-06-01", brokerage, 1_000_00, null).Source = TransactionSource.System;

        // July.
        l.Txn("2026-07-01", checking, 2_000_00, Rta);
        l.Txn("2026-07-10", checking, -60_00, groceries);
        l.Txn("2026-07-12", checking, -1_200_00, rent);

        // August.
        l.Txn("2026-08-01", checking, 3_000_00, Rta);
        l.Txn("2026-08-02", checking, 7_00, null);                 // uncategorized inflow
        l.Txn("2026-08-03", checking, -12_00, null);               // uncategorized outflow
        l.Txn("2026-08-05", visa, -50_00, groceries);
        l.Txn("2026-08-20", visa, -30_00, groceries);
        l.Txn("2026-08-21", visa, 10_00, groceries);               // refund
        l.Txn("2026-08-06", checking, -20_00, dining);
        l.Split("2026-08-07", checking, false, (groceries, null, -25_00), (dining, null, -15_00));
        l.Split("2026-08-08", checking, true, (groceries, null, -900_00), (dining, null, -99_00));
        l.Txn("2026-08-09", checking, -999_00, groceries, deleted: true);
        l.Txn("2026-08-12", checking, -1_200_00, rent);
        l.Transfer("2026-08-25", checking, visa, 200_00);         // card payment: never spending
        l.Transfer("2026-07-20", checking, brokerage, 500_00, fromCategory: investing);
        l.Txn("2026-08-01", mortgage, -300_00, interest);          // categorized row in a tracking account
        l.Transfer("2026-08-01", checking, mortgage, 800_00);      // uncategorized transfer to tracking
        await l.SaveAsync();

        await using (var db = l.Factory.CreateDbContext())
        {
            db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = brokerage, Date = D("2026-07-15"), Balance = 1_500_00, Source = BalanceSource.Manual });
            db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = brokerage, Date = D("2026-08-10"), Balance = 2_600_00, Source = BalanceSource.Manual });
            db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = checking, Date = D("2026-08-10"), Balance = 42, Source = BalanceSource.Provider });
            await db.SaveChangesAsync();
        }

        return new Ledger(l, new ReportService(l.Factory, l.Time), checking, visa, brokerage, mortgage, groceries, dining, rent, investing, interest, bills);
    }

    private static SpendingCategory Cat(SpendingReport report, Guid id) =>
        report.Groups.SelectMany(g => g.Categories).Single(c => c.CategoryId == id);

    [Fact]
    public async Task Spending_sums_categories_and_splits_and_compares_to_the_previous_period()
    {
        var x = await BuildAsync();
        var report = await x.Service.GetSpendingAsync(new ReportQuery(D("2026-08-01"), D("2026-08-31"), IncludeTransfers: false), Ct);

        report.PreviousFrom.ShouldBe(D("2026-07-01"));
        report.PreviousTo.ShouldBe(D("2026-07-31"));
        report.Currency.ShouldBe("USD");
        Cat(report, x.Groceries).Amount.ShouldBe(50_00 + 30_00 - 10_00 + 25_00);
        Cat(report, x.Groceries).PreviousAmount.ShouldBe(60_00);
        Cat(report, x.Dining).Amount.ShouldBe(35_00);
        Cat(report, x.Dining).PreviousAmount.ShouldBe(0);
        Cat(report, x.Rent).Amount.ShouldBe(1_200_00);
        Cat(report, x.Rent).PreviousAmount.ShouldBe(1_200_00);

        // Income, system rows, deleted rows and transfers are not spending.
        report.Groups.SelectMany(g => g.Categories).ShouldNotContain(c => c.CategoryId == Rta);
        report.Groups.SelectMany(g => g.Categories).ShouldNotContain(c => c.CategoryId == x.Investing);
        var uncategorized = report.Groups.Single(g => g.IsUncategorized);
        uncategorized.Amount.ShouldBe(12_00 - 7_00);

        var everyday = report.Groups.Single(g => g.Name == "Everyday");
        everyday.Amount.ShouldBe(95_00 + 35_00);
        everyday.Categories.Select(c => c.CategoryId).ShouldBe([x.Groceries, x.Dining]); // largest first
        report.Groups[0].GroupId.ShouldBe(x.Bills);                                        // largest first
        report.Total.ShouldBe(1_200_00 + 130_00 + 5_00);
        report.PreviousTotal.ShouldBe(1_260_00);
    }

    [Fact]
    public async Task Spending_transfer_and_tracking_toggles()
    {
        var x = await BuildAsync();
        var july = (D("2026-07-01"), D("2026-07-31"));
        var aug = (D("2026-08-01"), D("2026-08-31"));

        // The categorized transfer to the brokerage counts only when transfers are included.
        var with = await x.Service.GetSpendingAsync(new ReportQuery(july.Item1, july.Item2, IncludeTransfers: true), Ct);
        Cat(with, x.Investing).Amount.ShouldBe(500_00);
        var without = await x.Service.GetSpendingAsync(new ReportQuery(july.Item1, july.Item2, IncludeTransfers: false), Ct);
        without.Groups.SelectMany(g => g.Categories).ShouldNotContain(c => c.CategoryId == x.Investing);

        // Uncategorized transfers (card payment, checking to mortgage) never count, even when included.
        var augWith = await x.Service.GetSpendingAsync(new ReportQuery(aug.Item1, aug.Item2, IncludeTransfers: true), Ct);
        augWith.Groups.Single(g => g.IsUncategorized).Amount.ShouldBe(5_00);

        // Tracking-account rows count only when tracking accounts are included; their system opening rows never do.
        augWith.Groups.SelectMany(g => g.Categories).ShouldNotContain(c => c.CategoryId == x.Interest);
        var tracking = await x.Service.GetSpendingAsync(new ReportQuery(aug.Item1, aug.Item2, IncludeTransfers: true, IncludeTracking: true), Ct);
        Cat(tracking, x.Interest).Amount.ShouldBe(300_00);
        tracking.Groups.Single(g => g.IsUncategorized).Amount.ShouldBe(5_00);
        var juneTracking = await x.Service.GetSpendingAsync(new ReportQuery(D("2026-06-01"), D("2026-06-30"), IncludeTracking: true), Ct);
        juneTracking.Groups.ShouldBeEmpty();

        // Account filter.
        var visaOnly = await x.Service.GetSpendingAsync(new ReportQuery(aug.Item1, aug.Item2, [x.Visa]), Ct);
        visaOnly.Groups.ShouldHaveSingleItem().Categories.ShouldHaveSingleItem().Amount.ShouldBe(70_00);
    }

    [Fact]
    public async Task Previous_period_of_a_partial_range_is_the_same_number_of_days()
    {
        var x = await BuildAsync();
        var report = await x.Service.GetSpendingAsync(new ReportQuery(D("2026-08-05"), D("2026-08-14")), Ct);
        report.PreviousFrom.ShouldBe(D("2026-07-26"));
        report.PreviousTo.ShouldBe(D("2026-08-04"));
        Cat(report, x.Groceries).Amount.ShouldBe(50_00 + 25_00);
        Cat(report, x.Groceries).PreviousAmount.ShouldBe(0);
        report.Groups.Single(g => g.IsUncategorized).PreviousAmount.ShouldBe(12_00 - 7_00);
    }

    [Fact]
    public async Task Income_vs_expense_by_month_with_net()
    {
        var x = await BuildAsync();
        var report = await x.Service.GetIncomeExpenseAsync(new ReportQuery(D("2026-06-01"), D("2026-08-31")), Ct);

        report.Months.Select(m => m.Month).ShouldBe([M("2026-06"), M("2026-07"), M("2026-08")]);
        report.Months[0].ShouldBe(new IncomeExpenseMonth(M("2026-06"), 0, 0)); // opening balances are not income
        report.Months[1].Income.ShouldBe(2_000_00);
        report.Months[1].Expense.ShouldBe(60_00 + 1_200_00 + 500_00);         // transfers included by default
        report.Months[2].Income.ShouldBe(3_000_00 + 7_00);                     // uncategorized inflow is income
        report.Months[2].Expense.ShouldBe(12_00 + 95_00 + 35_00 + 1_200_00);
        report.Months[2].Net.ShouldBe(3_007_00 - 1_342_00);
        report.TotalNet.ShouldBe(report.TotalIncome - report.TotalExpense);

        var noTransfers = await x.Service.GetIncomeExpenseAsync(new ReportQuery(D("2026-07-01"), D("2026-07-31"), IncludeTransfers: false), Ct);
        noTransfers.Months.ShouldHaveSingleItem().Expense.ShouldBe(1_260_00);
        var tracking = await x.Service.GetIncomeExpenseAsync(new ReportQuery(D("2026-08-01"), D("2026-08-31"), IncludeTracking: true), Ct);
        tracking.Months.ShouldHaveSingleItem().Expense.ShouldBe(1_342_00 + 300_00);
    }

    [Fact]
    public async Task Net_worth_uses_the_ledger_and_the_latest_snapshot_on_or_before_each_point()
    {
        var x = await BuildAsync();
        var report = await x.Service.GetNetWorthAsync(new ReportQuery(D("2026-06-01"), D("2026-12-31"), IncludeTracking: true), Ct);

        // Month ends, clamped to "today" (2026-08-15 in TestLedger).
        report.Points.Select(p => p.Date).ShouldBe([D("2026-06-30"), D("2026-07-31"), D("2026-08-15")]);
        var brokerage = report.Accounts.Single(a => a.AccountId == x.Brokerage);
        brokerage.UsesSnapshots.ShouldBeTrue();
        // Jun: ledger (no snapshot yet). Jul: snapshot of 7/15 plus the 500 transferred on 7/20. Aug: snapshot of 8/10.
        brokerage.Balances.ShouldBe([1_000_00, 2_000_00, 2_600_00]);

        // On-budget accounts ignore provider snapshots: the ledger is the truth.
        var checking = report.Accounts.Single(a => a.AccountId == x.Checking);
        checking.UsesSnapshots.ShouldBeFalse();
        checking.Balances.ShouldBe([
            1_000_00,
            1_000_00 + 2_000_00 - 60_00 - 1_200_00 - 500_00,
            1_240_00 + 3_000_00 + 7_00 - 12_00 - 20_00 - 40_00 - 1_200_00 - 800_00]);
        report.Accounts.Single(a => a.AccountId == x.Visa).Balances.ShouldBe([0, 0, -50_00]); // rows after 8/15 (8/20 on) are after the point
        report.Accounts.Single(a => a.AccountId == x.Mortgage).Balances.ShouldBe([-100_000_00, -100_000_00, -100_000_00 - 300_00 + 800_00]);

        var aug = report.Points[2];
        aug.Assets.ShouldBe(checking.Balances[2] + 2_600_00);
        aug.Liabilities.ShouldBe(50_00 + 99_500_00);
        aug.NetWorth.ShouldBe(aug.Assets - aug.Liabilities);
        report.Accounts.Select(a => a.AccountId).ShouldBe([x.Checking, x.Visa, x.Brokerage, x.Mortgage]); // sidebar order

        // Without tracking accounts, and with an account filter.
        var onBudget = await x.Service.GetNetWorthAsync(new ReportQuery(D("2026-06-01"), D("2026-08-31")), Ct);
        onBudget.Accounts.Select(a => a.AccountId).ShouldBe([x.Checking, x.Visa]);
        onBudget.Points[2].NetWorth.ShouldBe(checking.Balances[2] - 50_00);
        var one = await x.Service.GetNetWorthAsync(new ReportQuery(D("2026-07-01"), D("2026-07-31"), [x.Brokerage], IncludeTracking: true), Ct);
        one.Points.ShouldHaveSingleItem().NetWorth.ShouldBe(2_000_00);
    }

    [Fact]
    public async Task Spending_matches_budget_activity_on_the_fixture()
    {
        await using var host = await LedgerTestHost.CreateAsync();
        var fixture = await LedgerFixtureGenerator.GenerateAsync(host.Factory, new LedgerFixtureOptions(3_000, Seed: 5), Ct);
        var budget = host.Get<IBudgetService>();
        var reports = host.Get<IReportService>();
        var month = new DateOnly(fixture.LastDate.Year, fixture.LastDate.Month, 1);

        var budgetMonth = await budget.GetMonthAsync(month, Ct);
        var spending = await reports.GetSpendingAsync(new ReportQuery(month, month.AddMonths(1).AddDays(-1)), Ct);
        foreach (var category in budgetMonth.Groups.Where(g => !g.IsSystem).SelectMany(g => g.Categories).Where(c => c.Activity.Amount != 0))
        {
            spending.Groups.SelectMany(g => g.Categories).Single(c => c.CategoryId == category.Id).Amount.ShouldBe(-category.Activity.Amount, category.Name);
        }
    }

    [Fact]
    public async Task Report_queries_over_100k_transactions_take_under_500_ms()
    {
        await using var host = await LedgerTestHost.CreateAsync();
        var fixture = await LedgerFixtureGenerator.GenerateAsync(host.Factory, new LedgerFixtureOptions(), Ct);
        var reports = host.Get<IReportService>();
        var to = fixture.LastDate;
        var year = new ReportQuery(new DateOnly(to.Year, to.Month, 1).AddMonths(-11), to);
        var all = new ReportQuery(fixture.FirstDate, to, IncludeTracking: true);

        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("Spending, 12 months", () => reports.GetSpendingAsync(year, Ct)),
            ("Spending, all history", () => reports.GetSpendingAsync(all with { IncludeTracking = false }, Ct)),
            ("Income vs expense, 12 months", () => reports.GetIncomeExpenseAsync(year, Ct)),
            ("Income vs expense, all history", () => reports.GetIncomeExpenseAsync(all, Ct)),
            ("Net worth, all history", () => reports.GetNetWorthAsync(all, Ct)),
        })
        {
            await run(); // warm
            var best = long.MaxValue;
            for (var i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew();
                await run();
                best = Math.Min(best, watch.ElapsedMilliseconds);
            }

            output.WriteLine($"{name}: best of 3 = {best} ms");
            best.ShouldBeLessThan(500, name);
        }

        var networth = await reports.GetNetWorthAsync(all, Ct);
        networth.Accounts.Count.ShouldBe(8);
        networth.Points.Count.ShouldBeGreaterThan(24);
        networth.Accounts.Single(a => a.Name == "Brokerage").UsesSnapshots.ShouldBeTrue();
    }
}
