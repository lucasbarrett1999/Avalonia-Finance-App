using Keel.Application.Forecast;
using Keel.Application.Scheduling;
using Keel.Domain;
using Keel.Domain.Forecast;
using Keel.Infrastructure.Forecast;
using Keel.Infrastructure.Tests.Ledger;

namespace Keel.Infrastructure.Tests.Recurring;

/// <summary>Forecast inputs, cache and settings on a real SQLite file (F-REP-4, ADR 0033, ADR 0035).</summary>
public sealed class ForecastServiceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;
    private M5TestLedger _ledger = null!;

    private static CancellationToken Ct => CancellationToken.None;

    private static DateOnly Today => M5TestLedger.Today;

    public async Task InitializeAsync()
    {
        _host = await LedgerTestHost.CreateAsync();
        _ledger = new M5TestLedger(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private IForecastService Forecast => _ledger.Forecast;

    [Fact]
    public async Task Forecast_starts_at_the_cleared_balance_and_adds_schedules_and_confirmed_items_only()
    {
        var checking = await _ledger.AccountAsync("Checking", opening: 1_000_00);
        await _ledger.AccountAsync("Visa", AccountType.CreditCard, 0);
        await _ledger.AddAsync(checking.Id, "Pending thing", -50_00, Today, status: TransactionStatus.Uncleared);
        await _ledger.MonthlyAsync(checking.Id, "Netflix", -15_00, 4, Today.AddDays(-5));
        await _ledger.MonthlyAsync(checking.Id, "Power Co", -80_00, 4, Today.AddDays(-7));
        await _ledger.Recurring.DetectAsync(Today, Ct);
        await _ledger.Recurring.ConfirmAsync((await _ledger.ItemAsync("Netflix")).Id, Ct);
        var payee = await _host.Payees.GetOrCreateAsync("Landlord", Ct);
        await _ledger.Scheduled.CreateAsync(new ScheduledTransactionEdit(checking.Id, payee.Id, -700_00, null, null, null, "FREQ=MONTHLY", Today.AddDays(3), null, false), Ct);

        var forecast = await Forecast.GetForecastAsync(new ForecastRequest(Floor: new Money(500_00, "USD")), Ct);
        forecast.Start.ShouldBe(Today);
        forecast.End.ShouldBe(Today.AddDays(90));
        forecast.Currency.ShouldBe("USD");
        var series = forecast.Accounts.ShouldHaveSingleItem("only the open on-budget cash account");
        var cleared = 1_000_00 - 4 * 15_00 - 4 * 80_00;
        series.Points[0].Balance.Amount.ShouldBe(cleared, "the uncleared row is not in the start");
        series.Points.Count.ShouldBe(91);
        series.Points[3].Balance.Amount.ShouldBe(cleared - 700_00);
        series.Points[3].HasEntries.ShouldBeTrue();
        var netflixDay = Today.AddDays(-5).AddMonths(1);
        series.Points.Single(p => p.Date == netflixDay).HasEntries.ShouldBeTrue();
        forecast.Skipped.ShouldContain(s => s.Reason == ForecastSkipReason.NotConfirmed && s.Label == "Power Co");
        series.LowestBalance.Amount.ShouldBeLessThan(cleared - 700_00);
        series.BelowFloor.ShouldNotBeEmpty();
        series.BelowFloor.ShouldAllBe(p => p.Balance.Amount < 500_00);
        forecast.Combined.Points.Select(p => p.Balance).ShouldBe(series.Points.Select(p => p.Balance));

        var explain = await Forecast.ExplainDayAsync(new ForecastRequest(Floor: new Money(500_00, "USD")), Today.AddDays(3), checking.Id, Ct);
        explain.Entries.ShouldHaveSingleItem().Label.ShouldBe("Landlord");
        explain.Closing.Amount.ShouldBe(explain.Opening.Amount - 700_00);
    }

    [Fact]
    public async Task Forecasts_are_cached_per_day_until_the_ledger_changes_or_invalidate()
    {
        var checking = await _ledger.AccountAsync("Checking", opening: 1_000_00);
        var service = _host.Get<ForecastService>();
        var request = new ForecastRequest();
        var first = await Forecast.GetForecastAsync(request, Ct);
        (await Forecast.GetForecastAsync(request, Ct)).ShouldBeSameAs(first);
        service.ComputeCount.ShouldBe(1);

        await _ledger.AddAsync(checking.Id, "Store", -100_00, Today.AddDays(-1));
        var second = await Forecast.GetForecastAsync(request, Ct);
        second.ShouldNotBeSameAs(first);
        second.Accounts[0].Points[0].Balance.Amount.ShouldBe(900_00);
        service.ComputeCount.ShouldBe(2);

        Forecast.Invalidate();
        (await Forecast.GetForecastAsync(request, Ct)).ShouldNotBeSameAs(second);
        service.ComputeCount.ShouldBe(3);
        (await Forecast.GetForecastAsync(request with { IncludeDiscretionarySpend = true }, Ct)).Discretionary.ShouldHaveSingleItem().TotalSpend.ShouldBe(100_00);
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        (await Forecast.GetSettingsAsync(Ct)).ShouldBe(ForecastSettings.Default);
        await Forecast.SaveSettingsAsync(new ForecastSettings(250_00, true), Ct);
        (await Forecast.GetSettingsAsync(Ct)).ShouldBe(new ForecastSettings(250_00, true));
    }
}
