using Keel.Application.Alerts;
using Keel.Application.Recurring;
using Keel.Domain;
using Keel.Domain.Alerts;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Recurring;

/// <summary>The notification center on a real SQLite file (F-REC-3, ADR 0032).</summary>
public sealed class AlertServiceTests : IAsyncLifetime
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

    private IAlertService Alerts => _ledger.Alerts;

    [Fact]
    public async Task Repeated_evaluations_store_each_alert_once_and_dismissed_alerts_stay_dismissed()
    {
        var checking = await _ledger.AccountAsync("Checking");
        await _ledger.MonthlyAsync(checking.Id, "Internet Co", -70_00, 6, Today.AddMonths(-1).AddDays(-6));
        await _ledger.Recurring.DetectAsync(Today.AddMonths(-1), Ct);
        var item = await _ledger.ItemAsync("Internet Co");
        await _ledger.Recurring.ConfirmAsync(item.Id, Ct);
        (await Alerts.GetAlertsAsync(false, Ct)).Select(a => a.Kind).ShouldBe([AlertKind.NewRecurring]);

        // The next charge never came: missing 3+ days past the expected date.
        var missing = await Alerts.EvaluateAsync(new AlertEvaluationRequest(Today, [], []), Ct);
        missing.Single().Kind.ShouldBe(AlertKind.MissingExpected);
        missing.Single().ExpectedDate.ShouldBe(item.NextExpectedDate);
        missing.Single().DaysLate.ShouldBe(Today.DayNumber - item.NextExpectedDate.DayNumber);
        missing.Single().RecurringItemId.ShouldBe(item.Id);
        missing.Single().PayeeName.ShouldBe("Internet Co");

        for (var i = 0; i < 3; i++)
        {
            (await Alerts.EvaluateAsync(new AlertEvaluationRequest(Today, [], [item.Id]), Ct)).ShouldBeEmpty("same keys, nothing new");
        }

        await using (var db = _host.Db())
        {
            (await db.Alerts.CountAsync()).ShouldBe(2);
            (await db.Alerts.Select(a => a.PayloadJson).ToListAsync()).Select(AlertPayload.FromJson).Select(p => p!.Key).Distinct().Count().ShouldBe(2);
        }

        (await Alerts.GetUnreadCountAsync(Ct)).ShouldBe(2);
        await Alerts.DismissAsync(missing.Single().Id, Ct);
        _host.Bus.Messages.OfType<AlertsChanged>().Last().UnreadCount.ShouldBe(1);
        (await Alerts.GetAlertsAsync(false, Ct)).Select(a => a.Kind).ShouldBe([AlertKind.NewRecurring]);
        (await Alerts.GetAlertsAsync(true, Ct)).Count.ShouldBe(2);
        (await Alerts.EvaluateAsync(new AlertEvaluationRequest(Today, [], []), Ct)).ShouldBeEmpty("a dismissed alert is never proposed again");

        var all = (await Alerts.GetAlertsAsync(true, Ct)).Select(a => a.Id).ToList();
        await Alerts.MarkReadAsync(all, Ct);
        (await Alerts.GetUnreadCountAsync(Ct)).ShouldBe(0);
        _host.Bus.Messages.OfType<AlertsChanged>().Last().UnreadCount.ShouldBe(0);
    }

    [Fact]
    public async Task New_item_alerts_for_items_created_outside_detection_and_trial_conversions()
    {
        var checking = await _ledger.AccountAsync("Checking");
        var trial = await _ledger.AddAsync(checking.Id, "StreamBox", 0, Today.AddDays(-20));
        var charge = await _ledger.AddAsync(checking.Id, "StreamBox", -9_99, Today.AddDays(-1));
        var payee = await _host.Payees.GetOrCreateAsync("StreamBox", Ct);
        var item = await _ledger.Recurring.CreateAsync(new RecurringItemEdit(payee.Id, checking.Id, RecurrenceCadence.Monthly, -9_99, Today.AddDays(29), null, true), Ct);

        var created = await Alerts.EvaluateAsync(new AlertEvaluationRequest(Today, [charge.Id], [item.Id]), Ct);
        created.Select(a => a.Kind).ShouldBe([AlertKind.NewRecurring, AlertKind.TrialConversion]);
        var conversion = created.Single(a => a.Kind == AlertKind.TrialConversion);
        conversion.TransactionId.ShouldBe(charge.Id);
        conversion.RecurringItemId.ShouldBe(item.Id);
        conversion.PreviousAmount!.Value.Amount.ShouldBe(0);
        trial.Amount.ShouldBe(0);

        (await Alerts.EvaluateAsync(new AlertEvaluationRequest(Today, [charge.Id], [item.Id]), Ct)).ShouldBeEmpty();
    }
}
