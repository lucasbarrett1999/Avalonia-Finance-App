using System.Diagnostics;
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

/// <summary>Age of money and budget health (F-REP-5, ADR 0094) on hand-built ledgers, plus the 100k timing.</summary>
[Collection(nameof(TimingCollection))]
public sealed class BudgetHealthReportTests(ITestOutputHelper output) : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private TestLedger? _ledger;

    public void Dispose() => _ledger?.Dispose();

    [Fact]
    public async Task Age_of_money_spends_on_budget_cash_first_in_first_out()
    {
        // TestLedger's clock is 2026-08-15.
        var l = _ledger = await CreateAsync();
        var checking = l.Account("Checking", AccountType.Checking);
        var savings = l.Account("Savings", AccountType.Savings);
        var visa = l.Account("Visa", AccountType.CreditCard);
        var brokerage = l.Account("Brokerage", AccountType.Investment);
        var groceries = l.Category("Groceries");
        var investing = l.Category("Investing");

        l.Txn("2026-06-01", checking, 1_000_00, Rta).Source = TransactionSource.System; // a starting balance is money in
        l.Txn("2026-07-01", checking, 2_000_00, Rta);
        l.Transfer("2026-07-05", checking, savings, 500_00);                              // between cash accounts: not counted
        l.Txn("2026-07-10", checking, -100_00, groceries);                                // 39 days (June 1 money)
        l.Txn("2026-07-20", visa, -50_00, groceries);                                     // card spending: counted when paid
        l.Transfer("2026-07-25", checking, visa, 200_00);                                 // card payment: 54 days
        l.Transfer("2026-08-01", checking, brokerage, 300_00, fromCategory: investing);   // to tracking: 61 days
        l.Split("2026-08-05", checking, false, (groceries, null, -40_00), (null, savings, -60_00)); // only the 40: 65 days
        l.Txn("2026-08-06", checking, -999_00, groceries, deleted: true);
        l.Txn("2026-08-10", savings, -30_00, groceries);                                  // 70 days
        l.Txn("2026-08-11", brokerage, -5_00, null);                                      // tracking account: not counted
        await l.SaveAsync();

        var service = new ReportService(l.Factory, l.Time);
        var report = await service.GetAgeOfMoneyAsync(D("2026-06-01"), D("2026-12-31"), Ct);

        report.Points.Select(p => p.Date).ShouldBe([D("2026-06-30"), D("2026-07-31"), D("2026-08-15")]);
        report.Points[0].Days.ShouldBeNull();
        report.Points[1].Days.ShouldBe(46);          // (39 + 54) / 2 = 46.5, half to even
        report.Latest!.Days.ShouldBe(58);             // (39 + 54 + 61 + 65 + 70) / 5 = 57.8
        report.Latest.Window.Select(w => w.AgeDays).ShouldBe([70m, 65m, 61m, 54m, 39m]);
        report.Latest.OutflowCount.ShouldBe(5);
    }

    [Fact]
    public async Task Age_of_money_uses_only_the_last_ten_outflows_and_skips_unfunded_spending()
    {
        var l = _ledger = await CreateAsync();
        var checking = l.Account("Checking", AccountType.Checking);
        var groceries = l.Category("Groceries");
        l.Txn("2026-07-01", checking, -50_00, groceries);                  // overdrawn: no inflow to fund it
        l.Txn("2026-07-02", checking, 1_050_00, Rta);                      // repays the 50 first
        for (var day = 3; day <= 14; day++)
        {
            l.Txn($"2026-07-{day:00}", checking, -10_00, groceries);       // ages 1..12
        }

        await l.SaveAsync();
        var latest = (await new ReportService(l.Factory, l.Time).GetAgeOfMoneyAsync(D("2026-07-01"), D("2026-07-31"), Ct)).Latest!;
        latest.OutflowCount.ShouldBe(10);
        latest.Days.ShouldBe(8); // mean of 3..12 = 7.5 -> 8
    }

    [Fact]
    public async Task Budget_health_reports_months_ahead_targets_funded_and_overspending()
    {
        var l = _ledger = await CreateAsync();
        var checking = l.Account("Checking", AccountType.Checking);
        var visa = l.Account("Visa", AccountType.CreditCard);
        var bills = EntityIds.New();
        await using (var db = l.Factory.CreateDbContext())
        {
            db.CategoryGroups.Add(new CategoryGroup { Id = bills, Name = "Bills", SortOrder = 3 });
            await db.SaveChangesAsync();
        }

        var groceries = l.Category("Groceries");
        var dining = l.Category("Dining");
        var fun = l.Category("Fun");
        var old = l.Category("Old", hidden: true);
        var rent = l.Category("Rent", bills);

        l.Txn("2026-05-01", checking, 5_000_00, Rta);
        l.Txn("2026-05-10", checking, -300_00, groceries);
        l.Txn("2026-06-10", checking, -600_00, groceries);
        l.Txn("2026-07-10", checking, -900_00, groceries);
        l.Assignment(groceries, "2026-05", 300_00);
        l.Assignment(groceries, "2026-06", 600_00);
        l.Assignment(groceries, "2026-07", 900_00);
        l.Assignment(groceries, "2026-08", 400_00);
        l.Assignment(rent, "2026-08", 1_000_00);
        l.Assignment(dining, "2026-08", 50_00);
        l.Txn("2026-08-03", checking, -100_00, groceries);
        l.Txn("2026-08-04", checking, -80_00, dining);   // cash overspent by 30
        l.Txn("2026-08-05", visa, -70_00, fun);          // credit overspent by 70
        l.Txn("2026-08-06", checking, -10_00, old);      // hidden: left out of the list
        await l.SaveAsync();
        await using (var db = l.Factory.CreateDbContext())
        {
            db.Targets.Add(new Keel.Domain.Entities.Target { CategoryId = groceries, Type = TargetType.MonthlySpending, Amount = 400_00, Cadence = RecurrenceCadence.Monthly });
            db.Targets.Add(new Keel.Domain.Entities.Target { CategoryId = rent, Type = TargetType.MonthlySetAside, Amount = 1_200_00, Cadence = RecurrenceCadence.Monthly });
            await db.SaveChangesAsync();
        }

        var service = new ReportService(l.Factory, l.Time, l.Service());
        var health = await service.GetBudgetHealthAsync(D("2026-08-20"), Ct);

        health.Month.ShouldBe(M("2026-08"));
        health.AsOf.ShouldBe(D("2026-08-15"));
        health.Currency.ShouldBe("USD");

        // Buffer = RTA (5,000 - 3,250) + Groceries 300 + Rent 1,000; spending averaged over May-July = 600.
        health.MonthsAhead.ReadyToAssign.ShouldBe(1_750_00);
        health.MonthsAhead.Buffer.ShouldBe(3_050_00);
        health.MonthsAhead.AverageSpending.ShouldBe(600_00);
        health.MonthsAhead.SpendingFrom.ShouldBe(M("2026-05"));
        health.MonthsAhead.SpendingTo.ShouldBe(D("2026-07-31"));
        health.MonthsAhead.Tenths.ShouldBe(50);

        // Groceries (refill to 400) is funded, Rent (1,200 a month) is 200 short: 1,400 of 1,600 = 87.5% -> 88%.
        health.Targets.ShouldBe(new TargetsFundedMetric(2, 1, 1_600_00, 200_00));
        health.Targets.Percent.ShouldBe(88);

        health.Overspent.Select(o => (o.Name, o.Available, o.IsCash)).ShouldBe([("Fun", -70_00L, false), ("Dining", -30_00L, true)]);
        health.OverspentCount.ShouldBe(2);
        health.CashOverspentCount.ShouldBe(1);
        health.AgeOfMoney.Points.Count.ShouldBe(12);
        health.AgeOfMoney.Latest!.Days.ShouldNotBeNull();

        // A past month describes its last day; a month without earlier history has no months-ahead figure.
        (await service.GetBudgetHealthAsync(M("2026-07"), Ct)).AsOf.ShouldBe(D("2026-07-31"));
        var first = await service.GetBudgetHealthAsync(M("2026-05"), Ct);
        first.MonthsAhead.Tenths.ShouldBeNull();
        first.MonthsAhead.AverageSpending.ShouldBe(0);
        new TargetsFundedMetric(0, 0, 0, 0).Percent.ShouldBeNull();
        new TargetsFundedMetric(1, 1, 0, 0).Percent.ShouldBe(100);

        await Should.ThrowAsync<InvalidOperationException>(() => new ReportService(l.Factory, l.Time).GetBudgetHealthAsync(M("2026-08"), Ct));
    }

    [Fact]
    public async Task Age_of_money_and_budget_health_over_100k_transactions_are_fast()
    {
        await using var host = await LedgerTestHost.CreateAsync();
        var fixture = await LedgerFixtureGenerator.GenerateAsync(host.Factory, new LedgerFixtureOptions(), Ct);
        var reports = host.Get<IReportService>();
        var month = new DateOnly(fixture.LastDate.Year, fixture.LastDate.Month, 1);

        // Generous limits (CI runners are slower): age of money is one GROUP BY day; health adds the budget month.
        foreach (var (name, limit, run) in new (string, long, Func<Task>)[]
        {
            ("Age of money, all history", 1_500, () => reports.GetAgeOfMoneyAsync(fixture.FirstDate, fixture.LastDate, Ct)),
            ("Budget health, last month", 3_000, () => reports.GetBudgetHealthAsync(month, Ct)),
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
            best.ShouldBeLessThan(limit, name);
        }

        var aom = await reports.GetAgeOfMoneyAsync(fixture.FirstDate, fixture.LastDate, Ct);
        aom.Latest!.Days.ShouldNotBeNull();
        aom.Latest.OutflowCount.ShouldBe(10);
        output.WriteLine($"Age of money on the fixture: {aom.Latest.Days} days");
    }
}
