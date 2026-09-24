using System.Diagnostics;
using Keel.Application.Budget;
using Keel.Application.Messaging;
using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Domain.Entities;
using Keel.Infrastructure.Budgeting;
using Keel.Infrastructure.Files;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using static Keel.Infrastructure.Tests.Budgeting.TestLedger;

namespace Keel.Infrastructure.Tests.Budgeting;

[Collection(nameof(TimingCollection))]
public sealed class BudgetServiceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private TestLedger? _ledger;

    public void Dispose() => _ledger?.Dispose();

    private static BudgetCategoryDto Row(BudgetMonthDto month, Guid id) =>
        month.Groups.SelectMany(g => g.Categories).Single(c => c.Id == id);

    private async Task<(TestLedger L, Guid Checking, Guid Visa, Guid Groceries, Guid Rent)> Example647Async()
    {
        var l = _ledger = await CreateAsync();
        var checking = l.Account("Checking", AccountType.Checking);
        var visa = l.Account("Visa", AccountType.CreditCard);
        var groceries = l.Category("Groceries");
        var rent = l.Category("Rent");
        l.Txn("2026-08-01", checking, 3_000_00, Rta);
        l.Txn("2026-08-05", checking, -1_500_00, rent);
        l.Txn("2026-08-10", visa, -250_00, groceries);
        l.Txn("2026-08-20", visa, -200_00, groceries);
        l.Transfer("2026-08-25", checking, visa, 100_00);
        await l.SaveAsync();
        var service = l.Service();
        await service.AssignAsync(rent, M("2026-08"), 1_500_00, Ct);
        await service.AssignAsync(groceries, M("2026-08"), 400_00, Ct);
        return (l, checking, visa, groceries, rent);
    }

    [Fact]
    public async Task Worked_example_6_4_7_through_the_database()
    {
        var (l, _, visa, groceries, rent) = await Example647Async();
        var service = l.Service();

        var aug = await service.GetMonthAsync(D("2026-08-17"), Ct);
        aug.Month.ShouldBe(M("2026-08"));
        aug.ReadyToAssign.ShouldBe(new Money(1_100_00, "USD"));
        Row(aug, groceries).Activity.Amount.ShouldBe(-450_00);
        Row(aug, groceries).Available.Amount.ShouldBe(-50_00);
        Row(aug, groceries).Overspending.ShouldBe(OverspendingKind.Credit);
        Row(aug, rent).Available.Amount.ShouldBe(0);
        var pay = Row(aug, l.PaymentCategories[visa]);
        pay.Kind.ShouldBe(BudgetCategoryKind.CreditCardPayment);
        pay.Activity.Amount.ShouldBe(300_00);
        pay.Available.Amount.ShouldBe(300_00);
        pay.CardPayment.ShouldNotBeNull();
        pay.CardPayment.Covered.Amount.ShouldBe(400_00);
        pay.CardPayment.Payments.Amount.ShouldBe(100_00);
        pay.CardPayment.CardBalance.Amount.ShouldBe(-350_00);
        pay.CardPayment.Difference.Amount.ShouldBe(-50_00);   // $50 not yet covered

        var sep = await service.GetMonthAsync(M("2026-09"), Ct);
        sep.ReadyToAssign.Amount.ShouldBe(1_100_00);
        Row(sep, groceries).Carry.Amount.ShouldBe(0);
        Row(sep, l.PaymentCategories[visa]).Carry.Amount.ShouldBe(300_00);

        // Assigning $50 more after the fact.
        await service.AssignAsync(groceries, M("2026-08"), 450_00, Ct);
        aug = await service.GetMonthAsync(M("2026-08"), Ct);
        Row(aug, groceries).Available.Amount.ShouldBe(0);
        Row(aug, l.PaymentCategories[visa]).CardPayment!.Covered.Amount.ShouldBe(450_00);
        Row(aug, l.PaymentCategories[visa]).Available.Amount.ShouldBe(350_00);
        Row(aug, l.PaymentCategories[visa]).CardPayment!.Difference.Amount.ShouldBe(0);
        aug.ReadyToAssign.Amount.ShouldBe(1_050_00);
    }

    [Fact]
    public async Task GetRange_matches_the_calculator_on_the_hand_built_ledger()
    {
        var hand = await BudgetAggregationQueryTests.BuildAsync();
        _ledger = hand.Ledger;
        await using (var db = _ledger.Factory.CreateDbContext())
        {
            var loaded = await BudgetAggregationQuery.LoadInputAsync(db, Ct);

            // The calculator on hand-written aggregates (not the query's output).
            var expected = BudgetCalculator.Compute(
                loaded with { Activity = hand.ExpectedActivity, CardTransfers = hand.ExpectedTransfers },
                M("2026-06"),
                M("2028-03"));

            var months = await _ledger.Service().GetRangeAsync(M("2026-06"), M("2028-03"), Ct);
            months.Count.ShouldBe(expected.Months.Count);
            foreach (var dto in months)
            {
                var month = expected.Month(dto.Month);
                dto.ReadyToAssign.Amount.ShouldBe(month.ReadyToAssign);
                dto.TotalAssigned.Amount.ShouldBe(month.TotalAssigned);
                dto.TotalActivity.Amount.ShouldBe(month.TotalActivity);
                dto.TotalAvailable.Amount.ShouldBe(month.TotalAvailable);
                dto.UncategorizedActivity.Amount.ShouldBe(month.UncategorizedActivity);
                foreach (var cell in month.Categories)
                {
                    var row = Row(dto, cell.CategoryId);
                    row.Assigned.Amount.ShouldBe(cell.Assigned);
                    row.Activity.Amount.ShouldBe(cell.Activity);
                    row.Available.Amount.ShouldBe(cell.Available);
                    row.Overspending.ShouldBe(BudgetDtoMapper.ToKind(cell.Overspending));
                }
            }
        }

        var aug = await _ledger.Service().GetMonthAsync(M("2026-08"), Ct);
        aug.ReadyToAssign.Amount.ShouldBe(3_000_00 - 400_00 - 50_00);
        aug.UncategorizedActivity.Amount.ShouldBe(-25_00);
        Row(aug, hand.Groceries).Available.Amount.ShouldBe(400_00 - 200_00 - 250_00);
    }

    [Fact]
    public async Task Assign_writes_audit_events_and_publishes_budget_changed()
    {
        var (l, _, _, groceries, _) = await Example647Async();
        var service = l.Service();
        l.Bus.Messages.Clear();

        await service.AssignAsync(groceries, D("2026-09-30"), 120_00, Ct);    // create
        await service.AssignAsync(groceries, M("2026-09"), 150_00, Ct);       // update
        await service.AssignAsync(groceries, M("2026-09"), 150_00, Ct);       // no change: nothing written
        await service.AssignAsync(groceries, M("2026-09"), 0, Ct);            // delete (absent row means 0)

        l.Bus.Messages.Count.ShouldBe(3);
        l.Bus.Messages.ShouldAllBe(m => m is BudgetChanged && ((BudgetChanged)m).Months.Single() == M("2026-09"));

        await using var db = l.Factory.CreateDbContext();
        (await db.BudgetAssignments.AnyAsync(a => a.Month == M("2026-09"))).ShouldBeFalse();
        var events = await db.AuditEvents.Where(e => e.EntityType == BudgetService.AssignmentEntityType && e.EntityId.EndsWith("2026-09-01"))
            .OrderBy(e => e.Id).ToListAsync();
        events.Select(e => e.Kind).ShouldBe([AuditEventKind.Created, AuditEventKind.Updated, AuditEventKind.Deleted]);
        events[0].BeforeJson.ShouldBeNull();
        events[0].AfterJson.ShouldBe($$"""{"CategoryId":"{{groceries}}","Month":"2026-09-01","Assigned":12000}""");
        events[1].BeforeJson.ShouldBe(events[0].AfterJson);
        events[1].AfterJson!.ShouldContain("\"Assigned\":15000");
        events[2].AfterJson.ShouldBeNull();
        events.ShouldAllBe(e => e.At == l.Time.GetUtcNow().UtcDateTime && e.EntityId == $"{groceries}/2026-09-01");
    }

    [Fact]
    public async Task Assign_rejects_inflow_and_unknown_categories()
    {
        var (l, _, _, _, _) = await Example647Async();
        var service = l.Service();
        await Should.ThrowAsync<InvalidOperationException>(() => service.AssignAsync(Rta, M("2026-08"), 1, Ct));
        await Should.ThrowAsync<KeyNotFoundException>(() => service.AssignAsync(Guid.NewGuid(), M("2026-08"), 1, Ct));
    }

    [Fact]
    public async Task Move_money_between_categories_and_ready_to_assign()
    {
        var (l, _, _, groceries, rent) = await Example647Async();
        var service = l.Service();
        l.Bus.Messages.Clear();

        await service.MoveMoneyAsync(new MoveMoneyRequest(M("2026-08"), rent, groceries, 50_00), Ct);
        var aug = await service.GetMonthAsync(M("2026-08"), Ct);
        Row(aug, rent).Assigned.Amount.ShouldBe(1_450_00);
        Row(aug, rent).Available.Amount.ShouldBe(-50_00);            // moving more than Available is allowed
        Row(aug, rent).Overspending.ShouldBe(OverspendingKind.Cash);
        Row(aug, groceries).Available.Amount.ShouldBe(0);
        aug.ReadyToAssign.Amount.ShouldBe(1_100_00);                 // category ↔ category leaves RTA alone

        await service.MoveMoneyAsync(new MoveMoneyRequest(M("2026-08"), null, rent, 50_00), Ct);   // from RTA
        (await service.GetMonthAsync(M("2026-08"), Ct)).ReadyToAssign.Amount.ShouldBe(1_050_00);
        await service.MoveMoneyAsync(new MoveMoneyRequest(M("2026-08"), groceries, null, 450_00), Ct); // to RTA (row deleted)
        aug = await service.GetMonthAsync(M("2026-08"), Ct);
        aug.ReadyToAssign.Amount.ShouldBe(1_500_00);
        Row(aug, groceries).Assigned.Amount.ShouldBe(0);

        l.Bus.Messages.Count.ShouldBe(3);
        await using var db = l.Factory.CreateDbContext();
        (await db.AuditEvents.CountAsync(e => e.EntityType == BudgetService.AssignmentEntityType)).ShouldBe(2 + 2 + 1 + 1);
        (await db.BudgetAssignments.AnyAsync(a => a.CategoryId == groceries)).ShouldBeFalse();
    }

    [Fact]
    public async Task Move_money_validates_its_request()
    {
        var (l, _, _, groceries, _) = await Example647Async();
        var service = l.Service();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => service.MoveMoneyAsync(new MoveMoneyRequest(M("2026-08"), groceries, null, 0), Ct));
        await Should.ThrowAsync<ArgumentException>(() => service.MoveMoneyAsync(new MoveMoneyRequest(M("2026-08"), groceries, groceries, 1), Ct));
        await Should.ThrowAsync<ArgumentException>(() => service.MoveMoneyAsync(new MoveMoneyRequest(M("2026-08"), null, null, 1), Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => service.MoveMoneyAsync(new MoveMoneyRequest(M("2026-08"), groceries, Rta, 1), Ct));
    }

    [Fact]
    public async Task Target_crud_with_audit_and_progress()
    {
        var (l, _, visa, groceries, rent) = await Example647Async();
        var service = l.Service();
        l.Bus.Messages.Clear();

        (await service.GetTargetAsync(groceries, Ct)).ShouldBeNull();
        await service.SetTargetAsync(new TargetDto(groceries, TargetType.MonthlySpending, 500_00), Ct);
        await service.SetTargetAsync(new TargetDto(groceries, TargetType.MonthlySpending, 500_00), Ct);   // unchanged: no-op
        await service.SetTargetAsync(new TargetDto(groceries, TargetType.MonthlySetAside, 450_00), Ct);
        (await service.GetTargetAsync(groceries, Ct)).ShouldBe(new TargetDto(groceries, TargetType.MonthlySetAside, 450_00));

        var aug = await service.GetMonthAsync(M("2026-08"), Ct);
        var progress = Row(aug, groceries).Target.ShouldNotBeNull();
        progress.Underfunded.Amount.ShouldBe(50_00);
        progress.NeededThisMonth.Amount.ShouldBe(450_00);
        Row(aug, rent).Target.ShouldBeNull();

        await service.SetTargetAsync(new TargetDto(l.PaymentCategories[visa], TargetType.DebtPayment, 200_00, LinkedAccountId: visa), Ct);
        var vacation = l.Category("Vacation");
        await l.SaveAsync();
        await service.SetTargetAsync(new TargetDto(vacation, TargetType.SavingsBalanceByDate, 3_000_00, D("2026-10-31")), Ct);
        Row(await service.GetMonthAsync(M("2026-08"), Ct), vacation).Target!.MonthlyNeed.Amount.ShouldBe(1_000_00);

        await service.DeleteTargetAsync(groceries, Ct);
        await service.DeleteTargetAsync(groceries, Ct);  // no-op
        (await service.GetTargetAsync(groceries, Ct)).ShouldBeNull();

        l.Bus.Messages.Count.ShouldBe(5);
        l.Bus.Messages.ShouldAllBe(m => ((BudgetChanged)m).Months.Single() == M("2026-08"));   // current month (FixedTimeProvider)
        await using var db = l.Factory.CreateDbContext();
        var events = await db.AuditEvents.Where(e => e.EntityType == BudgetService.TargetEntityType).OrderBy(e => e.Id).ToListAsync();
        events.Select(e => e.Kind).ShouldBe([AuditEventKind.Created, AuditEventKind.Updated, AuditEventKind.Created, AuditEventKind.Created, AuditEventKind.Deleted]);
        events[1].BeforeJson!.ShouldContain("\"Type\":\"MonthlySpending\"");
        events[1].AfterJson!.ShouldContain("\"Type\":\"MonthlySetAside\"");
    }

    [Fact]
    public async Task Target_validation()
    {
        var (l, checking, _, groceries, _) = await Example647Async();
        var service = l.Service();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => service.SetTargetAsync(new TargetDto(groceries, TargetType.MonthlySetAside, 0), Ct));
        await Should.ThrowAsync<ArgumentException>(() => service.SetTargetAsync(new TargetDto(groceries, TargetType.SavingsBalanceByDate, 1), Ct));
        await Should.ThrowAsync<ArgumentException>(() => service.SetTargetAsync(new TargetDto(groceries, TargetType.DebtPayment, 1), Ct));
        await Should.ThrowAsync<ArgumentException>(() => service.SetTargetAsync(new TargetDto(groceries, TargetType.DebtPayment, 1, LinkedAccountId: checking), Ct));
        await Should.ThrowAsync<ArgumentException>(() => service.SetTargetAsync(new TargetDto(groceries, TargetType.MonthlySetAside, 1, LinkedAccountId: Guid.NewGuid()), Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => service.SetTargetAsync(new TargetDto(Rta, TargetType.MonthlySetAside, 1), Ct));
    }

    [Fact]
    public async Task Fund_targets_is_limited_by_ready_to_assign_in_display_order()
    {
        var l = _ledger = await CreateAsync();
        var checking = l.Account("Checking", AccountType.Checking);
        var rent = l.Category("Rent");
        var groceries = l.Category("Groceries");
        var hidden = l.Category("Old", hidden: true);
        var funded = l.Category("Funded");
        l.Txn("2026-08-01", checking, 350_00, Rta);
        l.Assignment(rent, "2026-08", 50_00);
        await l.SaveAsync();
        var service = l.Service();
        await service.SetTargetAsync(new TargetDto(rent, TargetType.MonthlySetAside, 200_00), Ct);          // needs 150
        await service.SetTargetAsync(new TargetDto(groceries, TargetType.MonthlySpending, 400_00), Ct);     // needs 400
        await service.SetTargetAsync(new TargetDto(hidden, TargetType.MonthlySetAside, 100_00), Ct);        // hidden: skipped
        l.Bus.Messages.Clear();

        var result = await service.FundTargetsAsync(M("2026-08"), Ct);

        result.Funded.Amount.ShouldBe(300_00);
        result.Shortfall.Amount.ShouldBe(250_00);
        result.CategoriesFunded.ShouldBe(2);
        result.FullyFunded.ShouldBeFalse();
        var aug = await service.GetMonthAsync(M("2026-08"), Ct);
        aug.ReadyToAssign.Amount.ShouldBe(0);
        Row(aug, rent).Assigned.Amount.ShouldBe(200_00);
        Row(aug, groceries).Assigned.Amount.ShouldBe(150_00);
        Row(aug, groceries).Target!.Underfunded.Amount.ShouldBe(250_00);
        Row(aug, hidden).Assigned.Amount.ShouldBe(0);
        Row(aug, funded).Assigned.Amount.ShouldBe(0);
        l.Bus.Messages.Single().ShouldBeOfType<BudgetChanged>().Months.ShouldBe([M("2026-08")]);

        // Nothing left to fund: no change, no message.
        var again = await service.FundTargetsAsync(M("2026-08"), Ct);
        again.Funded.Amount.ShouldBe(0);
        again.Shortfall.Amount.ShouldBe(250_00);
        l.Bus.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Fund_targets_reports_fully_funded()
    {
        var l = _ledger = await CreateAsync();
        var checking = l.Account("Checking", AccountType.Checking);
        var rent = l.Category("Rent");
        l.Txn("2026-08-01", checking, 1_000_00, Rta);
        await l.SaveAsync();
        var service = l.Service();
        await service.SetTargetAsync(new TargetDto(rent, TargetType.MonthlySetAside, 600_00), Ct);

        var result = await service.FundTargetsAsync(M("2026-08"), Ct);
        result.FullyFunded.ShouldBeTrue();
        result.Funded.ShouldBe(new Money(600_00, "USD"));
        (await service.GetMonthAsync(M("2026-08"), Ct)).ReadyToAssign.Amount.ShouldBe(400_00);
    }

    [Fact]
    public async Task Quick_assign_and_explain()
    {
        var (l, checking, visa, groceries, _) = await Example647Async();
        var service = l.Service();
        await service.SetTargetAsync(new TargetDto(groceries, TargetType.MonthlySetAside, 500_00), Ct);

        var quick = await service.GetQuickAssignAsync(groceries, M("2026-09"), Ct);
        quick.AssignedLastMonth.Amount.ShouldBe(400_00);
        quick.SpentLastMonth.Amount.ShouldBe(450_00);
        quick.AverageAssigned.Amount.ShouldBe(133_33);
        quick.AverageSpent.Amount.ShouldBe(150_00);
        quick.FundTarget!.Value.Amount.ShouldBe(500_00);
        quick.ResetToZero.Amount.ShouldBe(0);

        var explanation = await service.ExplainAsync(l.PaymentCategories[visa], M("2026-08"), Ct);
        explanation.Name.ShouldBe("Pay_Visa");
        explanation.Total.Amount.ShouldBe(300_00);
        explanation.Lines.Where(x => x.IsTerm).Sum(x => x.Amount.Amount).ShouldBe(300_00);
        explanation.Lines.ShouldContain(x => x.Kind == ExplanationLineKind.CoveredFromCategory && x.CategoryName == "Groceries" && x.Amount.Amount == 400_00);
        explanation.Lines.ShouldContain(x => x.Kind == ExplanationLineKind.Payment && x.AccountId == checking && x.AccountName == "Checking" && x.Amount.Amount == -100_00);

        var rta = await service.ExplainAsync(null, M("2026-08"), Ct);
        rta.Name.ShouldBe(BudgetDtoMapper.ReadyToAssignName);
        rta.Total.Amount.ShouldBe(1_100_00);
    }

    [Fact]
    public async Task Currency_follows_the_first_on_budget_account()
    {
        var l = _ledger = await CreateAsync();
        await using (var db = l.Factory.CreateDbContext())
        {
            db.Accounts.Add(Account.Create("Girokonto", AccountType.Checking, D("2026-01-01"), "EUR"));
            await db.SaveChangesAsync();
        }

        (await l.Service().GetMonthAsync(M("2026-08"), Ct)).ReadyToAssign.Currency.ShouldBe("EUR");
        await Should.ThrowAsync<ArgumentException>(() => l.Service().GetRangeAsync(M("2026-08"), M("2026-07"), Ct));
    }

    [Fact]
    public async Task Is_registered_in_the_infrastructure_container()
    {
        using var temp = new TempDirectory();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IMessageBus, RecordingBus>()
            .AddKeelInfrastructure(new DataDirectory(temp.Path));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        provider.GetRequiredService<IBudgetService>().ShouldBeOfType<BudgetService>();
        provider.GetRequiredService<TimeProvider>().ShouldBe(TimeProvider.System);
    }

    [Fact]
    public async Task Month_switch_over_100k_transactions_is_fast()
    {
        var l = _ledger = await CreateAsync();
        var checking = l.Account("Checking", AccountType.Checking);
        var visa = l.Account("Visa", AccountType.CreditCard);
        var categories = Enumerable.Range(0, 60).Select(i => l.Category($"Category {i}")).ToList();
        await l.SaveAsync();

        // 100k rows over 36 months, inserted with one prepared statement in one transaction.
        var random = new Random(11);
        await using (var connection = new SqliteConnection(Persistence.KeelDatabase.ConnectionString(l.Factory.CurrentPath!)))
        {
            await connection.OpenAsync();
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();
            var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Transactions (Id, AccountId, Date, PayeeRaw, Amount, CategoryId, Status, IsApproved, Source, IsDeleted, CreatedAt, UpdatedAt)
                VALUES ($id, $account, $date, '', $amount, $category, 'Cleared', 1, 'Manual', 0, '2026-01-01 00:00:00', '2026-01-01 00:00:00')
                """;
            var id = cmd.Parameters.Add("$id", SqliteType.Text);
            var account = cmd.Parameters.Add("$account", SqliteType.Text);
            var date = cmd.Parameters.Add("$date", SqliteType.Text);
            var amount = cmd.Parameters.Add("$amount", SqliteType.Integer);
            var category = cmd.Parameters.Add("$category", SqliteType.Text);
            for (var i = 0; i < 100_000; i++)
            {
                id.Value = Guid.CreateVersion7().ToString().ToUpperInvariant();
                account.Value = (i % 3 == 0 ? visa : checking).ToString().ToUpperInvariant();
                date.Value = new DateOnly(2024, 1, 1).AddDays(random.Next(36 * 30)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                amount.Value = i % 50 == 0 ? random.NextInt64(1_000_00, 5_000_00) : -random.NextInt64(1_00, 200_00);
                category.Value = (i % 50 == 0 ? Rta : categories[i % categories.Count]).ToString().ToUpperInvariant();
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }

        var service = l.Service();
        await service.GetMonthAsync(M("2026-06"), Ct);  // warm-up
        var timings = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var month = await service.GetMonthAsync(M("2025-01").AddMonths(i), Ct);
            stopwatch.Stop();
            month.Groups.SelectMany(g => g.Categories).Count().ShouldBe(61);
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        output.WriteLine($"GetMonthAsync over 100k transactions (SQL aggregation + calculator + mapping): median {timings[2]:F1} ms, min {timings[0]:F1} ms, max {timings[^1]:F1} ms");
        timings[2].ShouldBeLessThan(2_000);   // generous bound for CI runners; the measured value is logged
    }
}
