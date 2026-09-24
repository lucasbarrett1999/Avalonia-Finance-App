using System.Globalization;
using Keel.Domain.Entities;
using Keel.Domain.Recurring;

namespace Keel.Domain.Tests.Recurring;

public class RecurringReconcilerTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 24);
    private static readonly Guid Checking = RecurringSeries.Account(1);
    private static readonly Guid Visa = RecurringSeries.Account(2);
    private static readonly Guid NetflixPayee = RecurringSeries.Id(6, 1);

    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static int _ids;

    private static Guid NextId() => RecurringSeries.Id(4, Interlocked.Increment(ref _ids));

    private static DetectedRecurringItem Netflix(string first = "2025-07-12", int count = 15, Guid? account = null) =>
        RecurringDetector.Detect(
            RecurringSeries.Build("NETFLIX", account ?? Visa, RecurringSeries.Dates(RecurrenceCadence.Monthly, D(first), count), -1_549),
            AsOf).Single();

    private static RecurringItem Stored(DetectedRecurringItem d, RecurringStatus status, Guid? account = null)
    {
        var item = new RecurringItem
        {
            Id = RecurringSeries.Id(4, 900 + (int)status),
            PayeeId = NetflixPayee,
            IsSubscription = true,
            CategoryId = RecurringSeries.Id(7, 1),
        };
        new RecurringItemValues(account ?? d.AccountId, d.Cadence, d.ExpectedAmount, d.AmountTolerance, d.IsVariableAmount, d.NextExpectedDate, d.LastSeenDate, d.Confidence, status)
            .ApplyTo(item);
        return item;
    }

    [Fact]
    public void A_new_pattern_is_created_as_detected()
    {
        var detection = Netflix();
        var decision = RecurringReconciler.Reconcile([detection], [], newId: () => RecurringSeries.Id(4, 1)).ShouldHaveSingleItem();

        decision.Kind.ShouldBe(RecurringDecisionKind.Create);
        decision.Reason.ShouldBe(RecurringDecisionReason.NewPattern);
        decision.ItemId.ShouldBe(RecurringSeries.Id(4, 1));
        decision.Before.ShouldBeNull();
        decision.After.ShouldBe(new RecurringItemValues(Visa, RecurrenceCadence.Monthly, -1_549, 200, false, D("2026-10-12"), D("2026-09-12"), 1.0, RecurringStatus.Detected));

        var created = decision.After!.ToNewItem(decision.ItemId!.Value, NetflixPayee);
        created.Id.ShouldBe(RecurringSeries.Id(4, 1));
        created.PayeeId.ShouldBe(NetflixPayee);
        created.Status.ShouldBe(RecurringStatus.Detected);
        created.NextExpectedDate.ShouldBe(D("2026-10-12"));
    }

    [Fact]
    public void Running_twice_updates_rather_than_duplicates()
    {
        var first = RecurringReconciler.Reconcile([Netflix(count: 14)], [], newId: NextId).Single();
        var item = first.After!.ToNewItem(first.ItemId!.Value, NetflixPayee);

        // A month later: one more occurrence.
        var second = RecurringReconciler.Reconcile([Netflix(count: 15)], [new ExistingRecurringItem(item, "NETFLIX")], newId: NextId).ShouldHaveSingleItem();
        second.Kind.ShouldBe(RecurringDecisionKind.Update);
        second.Reason.ShouldBe(RecurringDecisionReason.Changed);
        second.ItemId.ShouldBe(item.Id);
        second.ChangedFields.ShouldBe(["NextExpectedDate", "LastSeenDate"]);
        second.After!.Status.ShouldBe(RecurringStatus.Detected);

        second.After.ApplyTo(item);
        var third = RecurringReconciler.Reconcile([Netflix(count: 15)], [new ExistingRecurringItem(item, "NETFLIX")], newId: NextId).ShouldHaveSingleItem();
        third.Kind.ShouldBe(RecurringDecisionKind.NoOp);
        third.Reason.ShouldBe(RecurringDecisionReason.Unchanged);
        third.ItemId.ShouldBe(item.Id);
    }

    [Fact]
    public void Updates_keep_the_users_fields_and_status()
    {
        var detection = Netflix();
        var item = Stored(Netflix(count: 14), RecurringStatus.Active);
        item.ScheduledTransactionId = RecurringSeries.Id(11, 1);

        var decision = RecurringReconciler.Reconcile([detection], [new ExistingRecurringItem(item, "NETFLIX")]).Single();
        decision.After!.Status.ShouldBe(RecurringStatus.Active);
        decision.After.ApplyTo(item);

        item.IsSubscription.ShouldBeTrue();
        item.CategoryId.ShouldBe(RecurringSeries.Id(7, 1));
        item.ScheduledTransactionId.ShouldBe(RecurringSeries.Id(11, 1));
        item.PayeeId.ShouldBe(NetflixPayee);
        item.LastSeenDate.ShouldBe(D("2026-09-12"));
    }

    [Fact]
    public void Dismissed_items_are_never_recreated_or_updated()
    {
        var detection = Netflix();
        var dismissed = Stored(Netflix(count: 10), RecurringStatus.Dismissed);

        var decision = RecurringReconciler.Reconcile([detection], [new ExistingRecurringItem(dismissed, "NETFLIX")]).ShouldHaveSingleItem();

        decision.Kind.ShouldBe(RecurringDecisionKind.NoOp);
        decision.Reason.ShouldBe(RecurringDecisionReason.Dismissed);
        decision.ItemId.ShouldBe(dismissed.Id);
        decision.After.ShouldBeNull();
    }

    [Fact]
    public void Re_enabling_a_dismissed_payee_brings_the_item_back_as_detected()
    {
        var detection = Netflix();
        var dismissed = Stored(Netflix(count: 10), RecurringStatus.Dismissed);

        var decision = RecurringReconciler.Reconcile(
            [detection],
            [new ExistingRecurringItem(dismissed, "NETFLIX")],
            reenabledPayees: new HashSet<string> { "NETFLIX" }).ShouldHaveSingleItem();

        decision.Kind.ShouldBe(RecurringDecisionKind.Update);
        decision.Reason.ShouldBe(RecurringDecisionReason.Reenabled);
        decision.After!.Status.ShouldBe(RecurringStatus.Detected);
        decision.ChangedFields.ShouldContain("Status");
    }

    [Theory]
    [InlineData(RecurringStatus.Active, RecurringStatus.Ended, RecurringDecisionKind.Update, RecurringDecisionReason.Ended)]
    [InlineData(RecurringStatus.Detected, RecurringStatus.Ended, RecurringDecisionKind.Update, RecurringDecisionReason.Ended)]
    [InlineData(RecurringStatus.Paused, RecurringStatus.Paused, RecurringDecisionKind.NoOp, RecurringDecisionReason.Unchanged)]
    [InlineData(RecurringStatus.Ended, RecurringStatus.Ended, RecurringDecisionKind.NoOp, RecurringDecisionReason.Unchanged)]
    public void Lapsed_patterns_end_active_items(RecurringStatus stored, RecurringStatus expected, RecurringDecisionKind kind, RecurringDecisionReason reason)
    {
        var lapsed = Netflix(first: "2025-07-12", count: 7); // last 2026-01-12
        lapsed.IsLapsed.ShouldBeTrue();
        var item = Stored(lapsed, stored);

        var decision = RecurringReconciler.Reconcile([lapsed], [new ExistingRecurringItem(item, "NETFLIX")]).Single();

        decision.Kind.ShouldBe(kind);
        decision.Reason.ShouldBe(reason);
        (decision.After?.Status ?? item.Status).ShouldBe(expected);
    }

    [Fact]
    public void A_lapsed_pattern_without_an_item_creates_nothing()
    {
        var decision = RecurringReconciler.Reconcile([Netflix(count: 7)], []).ShouldHaveSingleItem();
        decision.Kind.ShouldBe(RecurringDecisionKind.NoOp);
        decision.Reason.ShouldBe(RecurringDecisionReason.Lapsed);
        decision.ItemId.ShouldBeNull();
    }

    [Fact]
    public void An_ended_item_whose_pattern_resumes_returns_as_detected()
    {
        var ended = Stored(Netflix(count: 7), RecurringStatus.Ended);
        var decision = RecurringReconciler.Reconcile([Netflix()], [new ExistingRecurringItem(ended, "NETFLIX")]).Single();
        decision.Kind.ShouldBe(RecurringDecisionKind.Update);
        decision.Reason.ShouldBe(RecurringDecisionReason.Resumed);
        decision.After!.Status.ShouldBe(RecurringStatus.Detected);
    }

    [Fact]
    public void Matches_by_payee_and_account_and_adopts_a_manual_item_without_account()
    {
        var onChecking = Netflix(account: Checking);
        var onVisa = Netflix();
        var manual = Stored(onVisa, RecurringStatus.Active, account: null);
        manual.AccountId = null;
        var otherPayee = Stored(onVisa, RecurringStatus.Active);
        otherPayee.Id = RecurringSeries.Id(4, 999);

        var decisions = RecurringReconciler.Reconcile(
            [onChecking, onVisa],
            [new ExistingRecurringItem(manual, "NETFLIX"), new ExistingRecurringItem(otherPayee, "HULU")],
            newId: () => RecurringSeries.Id(4, 500));

        // The item without an account is adopted by the first group; the second group gets a new item.
        decisions[0].Kind.ShouldBe(RecurringDecisionKind.Update);
        decisions[0].ItemId.ShouldBe(manual.Id);
        decisions[0].After!.AccountId.ShouldBe(Checking);
        decisions[0].ChangedFields.ShouldBe(["AccountId"]);
        decisions[1].Kind.ShouldBe(RecurringDecisionKind.Create);
        decisions[1].ItemId.ShouldBe(RecurringSeries.Id(4, 500));
        decisions[1].After!.AccountId.ShouldBe(Visa);
    }

    [Fact]
    public void Diff_names_every_changed_field()
    {
        var a = new RecurringItemValues(Visa, RecurrenceCadence.Monthly, -1_549, 200, false, D("2026-10-12"), D("2026-09-12"), 1.0, RecurringStatus.Active);
        var b = new RecurringItemValues(Checking, RecurrenceCadence.Yearly, -1_799, 300, true, D("2026-10-13"), D("2026-09-13"), 0.8, RecurringStatus.Paused);
        a.Diff(b).ShouldBe(["AccountId", "Cadence", "ExpectedAmount", "AmountTolerance", "IsVariableAmount", "NextExpectedDate", "LastSeenDate", "Confidence", "Status"]);
        a.Diff(a with { Confidence = 1.0 + 1e-12 }).ShouldBeEmpty();
        RecurringItemValues.From(a.ToNewItem(Guid.Empty, Guid.Empty)).ShouldBe(a);
    }

    [Fact]
    public void Realistic_fixture_reconciles_into_one_item_per_group()
    {
        var detections = RecurringDetector.Detect(RealisticLedger.Transactions(), RealisticLedger.AsOf);
        var decisions = RecurringReconciler.Reconcile(detections, [], newId: NextId);

        decisions.Count(d => d.Kind == RecurringDecisionKind.Create).ShouldBe(7); // the gym has lapsed
        decisions.Single(d => d.Kind == RecurringDecisionKind.NoOp).Detection.NormalizedPayee.ShouldBe("PLANET FITNESS CLUB FEES");

        var stored = decisions.Where(d => d.Kind == RecurringDecisionKind.Create)
            .Select(d => new ExistingRecurringItem(d.After!.ToNewItem(d.ItemId!.Value, Guid.Empty), d.Detection.NormalizedPayee))
            .ToList();
        var again = RecurringReconciler.Reconcile(detections, stored, newId: NextId);
        again.Count(d => d.Kind == RecurringDecisionKind.Create).ShouldBe(0);
        again.Count(d => d.Kind == RecurringDecisionKind.Update).ShouldBe(0);
    }
}
