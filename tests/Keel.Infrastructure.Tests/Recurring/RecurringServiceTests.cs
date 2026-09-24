using Keel.Application.Alerts;
using Keel.Application.Budget;
using Keel.Application.Recurring;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Recurring;

/// <summary>Recurring detection to stored items on a real SQLite file (F-REC-1, F-REC-2, F-REC-4).</summary>
public sealed class RecurringServiceTests : IAsyncLifetime
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

    private IRecurringService Recurring => _ledger.Recurring;

    [Fact]
    public async Task Detection_creates_detected_items_once_and_a_second_run_changes_nothing()
    {
        var checking = await _ledger.AccountAsync("Checking");
        var streaming = await _host.CategoryAsync("Streaming", "Subscriptions");
        await _ledger.MonthlyAsync(checking.Id, "Netflix", -15_49, 6, Today.AddDays(-5), streaming);
        await _ledger.EveryAsync(checking.Id, "Acme Payroll", 2_000_00, 8, 14, Today.AddDays(-3));
        await _ledger.AddAsync(checking.Id, "Corner Cafe", -4_50, Today.AddDays(-20));

        (await Recurring.GetLastDetectionDateAsync(Ct)).ShouldBeNull();
        var first = await Recurring.DetectAsync(Today, Ct);
        first.Created.ShouldBe(2);
        first.AlertsCreated.ShouldBe(2, "one new-item alert per created item");
        (await Recurring.GetLastDetectionDateAsync(Ct)).ShouldBe(Today);

        var netflix = await _ledger.ItemAsync("Netflix");
        netflix.Status.ShouldBe(RecurringStatus.Detected);
        netflix.Cadence.ShouldBe(RecurrenceCadence.Monthly);
        netflix.ExpectedAmount.Amount.ShouldBe(-15_49);
        netflix.AccountId.ShouldBe(checking.Id);
        netflix.CategoryId.ShouldBe(streaming, "the most common category of the occurrences");
        netflix.NextExpectedDate.ShouldBe(Today.AddDays(-5).AddMonths(1));
        netflix.Schedule.ShouldStartWith("Every month");
        var payroll = await _ledger.ItemAsync("Acme Payroll");
        payroll.Cadence.ShouldBe(RecurrenceCadence.Biweekly);
        payroll.ExpectedAmount.Amount.ShouldBe(2_000_00);

        _host.Bus.Messages.OfType<RecurringChanged>().ShouldNotBeEmpty();
        _host.Bus.Messages.OfType<AlertsChanged>().Last().UnreadCount.ShouldBe(2);
        _host.Undo.CanUndo.ShouldBeTrue("account and transaction entry");
        _host.Undo.NextUndo.ShouldBe(LedgerAction.AddTransaction, "an automatic detection run is not on the undo stack");

        var second = await Recurring.DetectAsync(Today, Ct);
        second.ShouldBe(new RecurringDetectionSummary(0, 0, 0, 2, 0, 0));
        await using var db = _host.Db();
        (await db.RecurringItems.CountAsync()).ShouldBe(2);
        (await db.Alerts.CountAsync()).ShouldBe(2);
        (await db.AuditEvents.CountAsync(a => a.EntityType == nameof(RecurringItem))).ShouldBe(2, "creation is audited");
    }

    [Fact]
    public async Task Dismissed_items_are_not_recreated_until_detection_is_reenabled_for_the_payee()
    {
        var checking = await _ledger.AccountAsync("Checking");
        await _ledger.MonthlyAsync(checking.Id, "Gym Club", -40_00, 5, Today.AddDays(-2));
        await Recurring.DetectAsync(Today, Ct);
        var gym = await _ledger.ItemAsync("Gym Club");

        await Recurring.DismissAsync(gym.Id, Ct);
        (await Recurring.GetItemsAsync(new RecurringItemFilter([]), Ct)).ShouldBeEmpty("the default list hides dismissed items");

        // A new occurrence and another run: the dismissed item stays dismissed and is not duplicated.
        await _ledger.AddAsync(checking.Id, "Gym Club", -40_00, Today);
        var run = await Recurring.DetectAsync(Today, Ct);
        run.Created.ShouldBe(0);
        run.SkippedDismissed.ShouldBe(1);
        (await _ledger.ItemAsync("Gym Club")).Status.ShouldBe(RecurringStatus.Dismissed);

        await Recurring.ReenableDetectionAsync(gym.PayeeId, Ct);
        var back = await _ledger.ItemAsync("Gym Club");
        back.Id.ShouldBe(gym.Id);
        back.Status.ShouldBe(RecurringStatus.Detected);
        back.LastSeenDate.ShouldBe(Today);
        await using var db = _host.Db();
        (await db.RecurringItems.CountAsync()).ShouldBe(1);
        (await db.Settings.SingleAsync(s => s.Key == "recurring.reenabledPayees")).ValueJson.ShouldBe("[]", "consumed once the item came back");
    }

    [Fact]
    public async Task Confirm_pause_resume_are_undoable_user_actions()
    {
        var checking = await _ledger.AccountAsync("Checking");
        await _ledger.MonthlyAsync(checking.Id, "City Water", -60_00, 4, Today.AddDays(-1));
        await Recurring.DetectAsync(Today, Ct);
        var water = await _ledger.ItemAsync("City Water");

        await Recurring.ConfirmAsync(water.Id, Ct);
        (await _ledger.ItemAsync("City Water")).Status.ShouldBe(RecurringStatus.Active);
        _host.Undo.NextUndo.ShouldBe(LedgerAction.ConfirmRecurring);
        await _host.Undo.UndoAsync(Ct);
        (await _ledger.ItemAsync("City Water")).Status.ShouldBe(RecurringStatus.Detected);
        await _host.Undo.RedoAsync(Ct);

        await Recurring.PauseAsync(water.Id, Ct);
        (await _ledger.ItemAsync("City Water")).Status.ShouldBe(RecurringStatus.Paused);
        (await Recurring.DetectAsync(Today, Ct)).Unchanged.ShouldBe(1, "detection keeps a paused item paused");
        await Recurring.ResumeAsync(water.Id, Ct);
        (await _ledger.ItemAsync("City Water")).Status.ShouldBe(RecurringStatus.Active);
    }

    [Fact]
    public async Task Totals_split_subscriptions_by_the_designated_group_and_create_target_sets_aside_the_monthly_amount()
    {
        var checking = await _ledger.AccountAsync("Checking");
        var streaming = await _host.CategoryAsync("Streaming", "Subscriptions");
        var power = await _host.CategoryAsync("Electric", "Bills");
        await _ledger.MonthlyAsync(checking.Id, "Netflix", -15_00, 6, Today.AddDays(-4), streaming);
        await _ledger.MonthlyAsync(checking.Id, "Power Co", -90_00, 6, Today.AddDays(-6), power);
        await _ledger.EveryAsync(checking.Id, "Acme Payroll", 1_000_00, 6, 14, Today.AddDays(-2));
        await Recurring.DetectAsync(Today, Ct);

        var none = await Recurring.GetTotalsAsync(includeUnconfirmed: true, Ct);
        none.SubscriptionsMonthly.Amount.ShouldBe(0);
        none.BillsMonthly.Amount.ShouldBe(-105_00);
        none.MonthlyOutflow.Amount.ShouldBe(-105_00);
        none.MonthlyInflow.Amount.ShouldBe(2_166_67, "biweekly 1,000 x 26 / 12, half to even");
        (await Recurring.GetTotalsAsync(includeUnconfirmed: false, Ct)).ItemCount.ShouldBe(0, "nothing confirmed yet");

        var groups = (await _host.Categories.GetCategoriesAsync(false, Ct)).Where(c => c.GroupName == "Subscriptions").Select(c => c.GroupId).Distinct().ToList();
        await Recurring.SetSubscriptionDesignationsAsync(new SubscriptionDesignations(groups, []), Ct);
        (await Recurring.GetSubscriptionDesignationsAsync(Ct)).GroupIds.ShouldBe(groups);
        (await _ledger.ItemAsync("Netflix")).IsSubscription.ShouldBeTrue();
        var totals = await Recurring.GetTotalsAsync(includeUnconfirmed: true, Ct);
        totals.SubscriptionsMonthly.Amount.ShouldBe(-15_00);
        totals.SubscriptionsYearly.Amount.ShouldBe(-180_00);
        totals.BillsMonthly.Amount.ShouldBe(-90_00);
        totals.BillsYearly.Amount.ShouldBe(-1_080_00);
        (await Recurring.GetItemsAsync(new RecurringItemFilter([], RecurringItemKind.Subscriptions), Ct)).Select(i => i.PayeeName).ShouldBe(["Netflix"]);
        (await Recurring.GetItemsAsync(new RecurringItemFilter([], RecurringItemKind.Income), Ct)).Select(i => i.PayeeName).ShouldBe(["Acme Payroll"]);

        var netflix = await _ledger.ItemAsync("Netflix");
        await Recurring.CreateTargetAsync(netflix.Id, Ct);
        var target = (await _host.Get<IBudgetService>().GetTargetAsync(streaming, Ct))!;
        target.Type.ShouldBe(TargetType.MonthlySetAside);
        target.Amount.ShouldBe(15_00);
    }

    [Fact]
    public async Task Manual_items_edits_occurrences_and_detail_history()
    {
        var checking = await _ledger.AccountAsync("Checking");
        var rent = await _host.CategoryAsync("Rent", "Bills");
        await _ledger.MonthlyAsync(checking.Id, "Landlord", -1_500_00, 3, Today.AddDays(-10), rent);
        var payee = await _host.Payees.GetOrCreateAsync("Landlord", Ct);
        var created = await Recurring.CreateAsync(new RecurringItemEdit(payee.Id, checking.Id, RecurrenceCadence.Monthly, -1_500_00, Today.AddDays(-10).AddMonths(1), rent, false), Ct);
        created.Status.ShouldBe(RecurringStatus.Active);
        created.LastSeenDate.ShouldBe(Today.AddDays(-10));
        _host.Undo.NextUndo.ShouldBe(LedgerAction.CreateRecurring);

        var occurrences = await Recurring.GetOccurrencesAsync(Today.AddDays(-10).AddMonths(-1), Today.AddDays(-10).AddMonths(2), Ct);
        occurrences.Where(o => !o.IsExpected).Select(o => o.Date).ShouldBe([Today.AddDays(-10).AddMonths(-1), Today.AddDays(-10)]);
        occurrences.Where(o => o.IsExpected).Select(o => o.Date).ShouldBe([Today.AddDays(-10).AddMonths(1), Today.AddDays(-10).AddMonths(2)]);

        var edited = await Recurring.UpdateAsync(created.Id, new RecurringItemEdit(payee.Id, checking.Id, RecurrenceCadence.Monthly, -1_550_00, created.NextExpectedDate, rent, false), Ct);
        edited.ExpectedAmount.Amount.ShouldBe(-1_550_00);
        edited.AmountTolerance.Amount.ShouldBe(155_00);

        var detail = (await Recurring.GetItemAsync(created.Id, Ct))!;
        detail.History.Count.ShouldBe(3);
        detail.History.ShouldAllBe(h => h.Amount.Amount == -1_500_00 && h.TransactionId != null);
        detail.Scores.Single(s => s.Cadence == RecurrenceCadence.Monthly).Fraction.ShouldBe(1);

        // Detection adopts the manual item for the same payee and account instead of duplicating it.
        (await Recurring.DetectAsync(Today, Ct)).Created.ShouldBe(0);
    }

    [Fact]
    public async Task Detection_after_an_import_alerts_a_price_increase_of_a_confirmed_item_once()
    {
        var checking = await _ledger.AccountAsync("Checking");
        await _ledger.MonthlyAsync(checking.Id, "Spotify", -10_99, 6, Today.AddDays(-30));
        await Recurring.DetectAsync(Today.AddDays(-25), Ct);
        await Recurring.ConfirmAsync((await _ledger.ItemAsync("Spotify")).Id, Ct);
        await _ledger.Alerts.MarkReadAsync((await _ledger.Alerts.GetAlertsAsync(false, Ct)).Select(a => a.Id).ToList(), Ct);

        var charge = await _ledger.AddAsync(checking.Id, "Spotify", -12_99, Today.AddDays(-30).AddMonths(1) > Today ? Today : Today.AddDays(-30).AddMonths(1));
        var summary = await Recurring.DetectAfterImportAsync(Today, [charge.Id], Ct);
        summary.AlertsCreated.ShouldBe(1);
        var alert = (await _ledger.Alerts.GetAlertsAsync(false, Ct)).First();
        alert.Kind.ShouldBe(AlertKind.PriceIncrease);
        alert.PayeeName.ShouldBe("Spotify");
        alert.Amount!.Value.Amount.ShouldBe(-12_99);
        alert.PreviousAmount!.Value.Amount.ShouldBe(-10_99);
        alert.IncreasePercent.ShouldBe(18.19m);
        (await _ledger.Alerts.GetUnreadCountAsync(Ct)).ShouldBe(1);

        (await Recurring.DetectAfterImportAsync(Today, [charge.Id], Ct)).AlertsCreated.ShouldBe(0, "idempotent by key");
        (await Recurring.RunDailyAsync(Today, Ct)).ShouldBeNull("detection already ran today");
        (await Recurring.RunDailyAsync(Today.AddDays(1), Ct)).ShouldNotBeNull();
    }
}
