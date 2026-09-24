using System.Globalization;
using Keel.Domain.Alerts;
using Keel.Domain.Entities;
using Keel.Domain.Import;
using Keel.Domain.Recurring;
using Keel.Domain.Tests.Recurring;

namespace Keel.Domain.Tests.Alerts;

public class AlertEvaluatorTests
{
    private static readonly Guid Checking = RecurringSeries.Account(1);
    private static readonly Guid Visa = RecurringSeries.Account(2);
    private static readonly HashSet<string> NoKeys = [];

    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static int _n;

    private static RecurringTransaction T(string payee, Guid account, string date, long amount) =>
        new(RecurringSeries.Id(20, Interlocked.Increment(ref _n)), account, D(date), amount, payee);

    private static AlertRecurringItem Item(
        string payee,
        Guid? account,
        long expected,
        string next,
        string lastSeen,
        RecurringStatus status = RecurringStatus.Active,
        RecurrenceCadence cadence = RecurrenceCadence.Monthly,
        bool variable = false,
        int id = 1) =>
        new(RecurringSeries.Id(21, id), payee, account, cadence, expected, variable, D(next), D(lastSeen), status);

    private static AlertEvaluationInput Input(
        string today,
        IReadOnlyList<AlertRecurringItem> items,
        IReadOnlyList<RecurringTransaction>? history = null,
        IReadOnlyList<RecurringTransaction>? batch = null,
        IReadOnlyList<RecurringDecision>? decisions = null,
        IReadOnlySet<string>? keys = null) =>
        new(D(today), items, history ?? [], batch ?? [], decisions ?? [], keys ?? NoKeys);

    // Price increase ----------------------------------------------------------------------------

    [Theory]
    [InlineData(-1_549, -1_799, true)]     // +$2.50, +16%
    [InlineData(-1_000, -1_060, false)]    // +6% but only $0.60
    [InlineData(-10_000, -10_400, false)]  // +$4 but only 4%
    [InlineData(-10_000, -10_500, false)]  // exactly 5% is not more than 5%
    [InlineData(-10_000, -10_501, true)]
    [InlineData(-2_000, -2_100, false)]    // exactly $1 is not more than $1
    [InlineData(-2_000, -2_101, true)]
    [InlineData(-1_799, -1_549, false)]    // a decrease
    [InlineData(245_000, 260_000, false)]  // a raise is not a price increase
    [InlineData(0, -1_549, false)]
    public void Price_increase_rule(long previous, long current, bool expected) =>
        AlertEvaluator.IsPriceIncrease(previous, current).ShouldBe(expected);

    [Fact]
    public void Netflix_price_increase_is_reported_against_the_previous_charge()
    {
        var history = new[] { T("NETFLIX", Visa, "2026-03-12", -1_549), T("NETFLIX", Visa, "2026-04-12", -1_549) };
        var charge = T("NETFLIX", Visa, "2026-05-12", -1_799);
        var item = Item("NETFLIX", Visa, -1_549, "2026-05-12", "2026-04-12");

        var alert = AlertEvaluator.Evaluate(Input("2026-05-13", [item], history, [charge])).ShouldHaveSingleItem();

        alert.Kind.ShouldBe(AlertKind.PriceIncrease);
        alert.RecurringItemId.ShouldBe(item.Id);
        alert.TransactionId.ShouldBe(charge.Id);
        alert.Key.ShouldBe(AlertKeys.PriceIncrease(item.Id, charge.Id));
        alert.Payload.PreviousAmount.ShouldBe(-1_549);
        alert.Payload.Amount.ShouldBe(-1_799);
        alert.Payload.IncreaseBasisPoints.ShouldBe(1_613);
        alert.Payload.Date.ShouldBe(D("2026-05-12"));
        alert.Payload.Payee.ShouldBe("NETFLIX");

        // The following month at the new price is not another increase.
        var june = T("NETFLIX", Visa, "2026-06-12", -1_799);
        AlertEvaluator.Evaluate(Input("2026-06-13", [item], [.. history, charge], [june])).ShouldBeEmpty();
    }

    [Fact]
    public void Without_history_the_expected_amount_is_the_previous_amount()
    {
        var item = Item("SPOTIFY", Checking, -1_199, "2026-09-28", "2026-08-28");
        var charge = T("SPOTIFY", Checking, "2026-09-28", -1_399);
        AlertEvaluator.Evaluate(Input("2026-09-28", [item], batch: [charge])).ShouldHaveSingleItem().Payload.PreviousAmount.ShouldBe(-1_199);
    }

    [Fact]
    public void Two_charges_in_one_batch_compare_with_each_other()
    {
        var item = Item("GYM", Checking, -2_499, "2026-08-05", "2026-07-05");
        var aug = T("GYM", Checking, "2026-08-05", -2_499);
        var sep = T("GYM", Checking, "2026-09-05", -2_999);
        var alert = AlertEvaluator.Evaluate(Input("2026-09-06", [item], batch: [sep, aug])).ShouldHaveSingleItem();
        alert.TransactionId.ShouldBe(sep.Id);
        alert.Payload.PreviousAmount.ShouldBe(-2_499);
    }

    [Theory]
    [InlineData(RecurringStatus.Paused)]
    [InlineData(RecurringStatus.Ended)]
    [InlineData(RecurringStatus.Dismissed)]
    public void No_price_alert_for_inactive_items(RecurringStatus status)
    {
        var item = Item("NETFLIX", Visa, -1_549, "2026-05-12", "2026-04-12", status);
        AlertEvaluator.Evaluate(Input("2026-05-13", [item], [T("NETFLIX", Visa, "2026-04-12", -1_549)], [T("NETFLIX", Visa, "2026-05-12", -1_799)]))
            .ShouldBeEmpty();
    }

    [Fact]
    public void No_price_alert_for_variable_items_other_accounts_inflows_or_unknown_payees()
    {
        var utility = Item("PGE", Checking, -15_000, "2026-09-21", "2026-08-21", variable: true, id: 1);
        var netflix = Item("NETFLIX", Visa, -1_549, "2026-05-12", "2026-04-12", id: 2);
        var pay = Item("ACME", Checking, 245_000, "2026-10-02", "2026-09-18", cadence: RecurrenceCadence.Biweekly, id: 3);
        var history = new[] { T("PGE", Checking, "2026-08-21", -12_000), T("NETFLIX", Visa, "2026-04-12", -1_549), T("ACME", Checking, "2026-09-18", 245_000) };
        var batch = new[]
        {
            T("PGE", Checking, "2026-09-21", -19_000),       // variable amount
            T("NETFLIX", Checking, "2026-05-12", -1_799),    // another account than the item's
            T("ACME", Checking, "2026-10-02", 260_000),      // an inflow
            T("HULU", Visa, "2026-05-12", -9_999),           // no item
        };
        AlertEvaluator.PriceIncreases([utility, netflix, pay], history, batch).ShouldBeEmpty();
    }

    [Fact]
    public void An_item_without_an_account_matches_any_account()
    {
        var item = Item("NETFLIX", null, -1_549, "2026-05-12", "2026-04-12");
        var alert = AlertEvaluator.Evaluate(Input("2026-05-13", [item], batch: [T("NETFLIX", Checking, "2026-05-12", -1_799)])).ShouldHaveSingleItem();
        alert.Kind.ShouldBe(AlertKind.PriceIncrease);
    }

    // Missing -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-10-01", false)]
    [InlineData("2026-10-03", false)]
    [InlineData("2026-10-04", true)]
    [InlineData("2026-10-20", true)]
    public void Missing_three_or_more_days_past_the_expected_date(string today, bool expected)
    {
        var rent = Item("RENT", Checking, -185_000, "2026-10-01", "2026-09-01");
        var alerts = AlertEvaluator.Evaluate(Input(today, [rent], [T("RENT", Checking, "2026-09-01", -185_000)]));
        if (!expected)
        {
            alerts.ShouldBeEmpty();
            return;
        }

        var alert = alerts.ShouldHaveSingleItem();
        alert.Kind.ShouldBe(AlertKind.MissingExpected);
        alert.Key.ShouldBe(AlertKeys.MissingExpected(rent.Id, D("2026-10-01")));
        alert.RecurringItemId.ShouldBe(rent.Id);
        alert.TransactionId.ShouldBeNull();
        alert.Payload.ExpectedDate.ShouldBe(D("2026-10-01"));
        alert.Payload.DaysLate.ShouldBe(D(today).DayNumber - D("2026-10-01").DayNumber);
        alert.Payload.Amount.ShouldBe(-185_000);
    }

    [Theory]
    [InlineData("2026-09-30")]   // early, inside the monthly window
    [InlineData("2026-10-02")]   // late, before today
    public void Not_missing_when_the_payment_arrived(string paid)
    {
        var rent = Item("RENT", Checking, -185_000, "2026-10-01", "2026-09-01");
        AlertEvaluator.Evaluate(Input("2026-10-05", [rent], [T("RENT", Checking, "2026-09-01", -185_000)], [T("RENT", Checking, paid, -185_000)]))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Missing_ignores_unrelated_rows_and_unconfirmed_items()
    {
        var rent = Item("RENT", Checking, -185_000, "2026-10-01", "2026-09-01", id: 1);
        var detected = Item("GYM", Checking, -2_499, "2026-09-05", "2026-08-05", RecurringStatus.Detected, id: 2);
        var paused = Item("HULU", Checking, -999, "2026-09-05", "2026-08-05", RecurringStatus.Paused, id: 3);
        var batch = new[]
        {
            T("RENT", Visa, "2026-10-01", -185_000),         // other account
            T("RENT", Checking, "2026-10-01", 185_000),      // refund, wrong direction
            T("RENT", Checking, "2026-09-01", -185_000),     // the last seen occurrence itself
        };
        AlertEvaluator.Evaluate(Input("2026-10-10", [rent, detected, paused], batch: batch))
            .ShouldHaveSingleItem().RecurringItemId.ShouldBe(rent.Id);
    }

    [Fact]
    public void A_weekly_payment_before_the_early_window_does_not_count()
    {
        var item = Item("PAY", Checking, 100_000, "2026-09-11", "2026-09-04", cadence: RecurrenceCadence.Weekly);
        // Due on the 11th; the weekly window starts on the 9th, and the last seen row is the 4th.
        AlertEvaluator.Evaluate(Input("2026-09-14", [item], batch: [T("PAY", Checking, "2026-09-04", 100_000)])).ShouldHaveSingleItem();
        AlertEvaluator.Evaluate(Input("2026-09-14", [item], batch: [T("PAY", Checking, "2026-09-09", 100_000)])).ShouldBeEmpty();
    }

    // New recurring -----------------------------------------------------------------------------

    [Fact]
    public void New_items_from_a_detection_run()
    {
        var detections = RecurringDetector.Detect(RealisticLedger.Transactions(), RealisticLedger.AsOf);
        var id = 0;
        var decisions = RecurringReconciler.Reconcile(detections, [], newId: () => RecurringSeries.Id(22, ++id));

        var alerts = AlertEvaluator.Evaluate(Input("2026-09-24", [], decisions: decisions));

        alerts.Count.ShouldBe(7); // every created item; the lapsed gym creates nothing
        alerts.ShouldAllBe(a => a.Kind == AlertKind.NewRecurring && a.TransactionId == null);
        alerts.Select(a => a.RecurringItemId).ShouldBe(decisions.Where(d => d.Kind == RecurringDecisionKind.Create).Select(d => d.ItemId).OrderBy(i => i), ignoreOrder: true);
        var rent = alerts.Single(a => a.Payload.Payee == PayeeNormalizer.Normalize(RealisticLedger.RentRaw));
        rent.Payload.Cadence.ShouldBe(RecurrenceCadence.Monthly);
        rent.Payload.Amount.ShouldBe(-185_000);
        rent.Payload.NextExpectedDate.ShouldBe(D("2026-10-01"));
    }

    // Trial conversion --------------------------------------------------------------------------

    [Theory]
    [InlineData(0L)]
    [InlineData(-100L)]
    [InlineData(-1L)]
    public void First_charge_after_a_trial(long trialAmount)
    {
        var trial = T("HULU", Visa, "2026-08-01", trialAmount);
        var charge = T("HULU", Visa, "2026-08-31", -1_499);

        var alert = AlertEvaluator.Evaluate(Input("2026-09-01", [], [trial], [charge])).ShouldHaveSingleItem();

        alert.Kind.ShouldBe(AlertKind.TrialConversion);
        alert.Key.ShouldBe(AlertKeys.TrialConversion(charge.Id));
        alert.TransactionId.ShouldBe(charge.Id);
        alert.RecurringItemId.ShouldBeNull();
        alert.Payload.TrialTransactionId.ShouldBe(trial.Id);
        alert.Payload.PreviousAmount.ShouldBe(trialAmount);
        alert.Payload.Amount.ShouldBe(-1_499);
    }

    [Fact]
    public void Trial_conversion_links_the_item_and_checks_any_account()
    {
        var item = Item("HULU", Checking, -1_499, "2026-09-30", "2026-08-31", RecurringStatus.Detected);
        var alerts = AlertEvaluator.Evaluate(Input("2026-09-01", [item], [T("HULU", Visa, "2026-08-01", 0)], [T("HULU", Checking, "2026-08-31", -1_499)]));
        alerts.ShouldHaveSingleItem().RecurringItemId.ShouldBe(item.Id);
    }

    [Fact]
    public void No_trial_conversion_for_later_charges_old_trials_or_small_charges()
    {
        var trial = T("HULU", Visa, "2026-05-01", 0);
        var first = T("HULU", Visa, "2026-05-31", -1_499);
        var second = T("HULU", Visa, "2026-06-30", -1_499);
        AlertEvaluator.TrialConversions([], [trial, first], [second]).ShouldBeEmpty();

        var old = T("DISNEY", Visa, "2026-01-01", 0);
        AlertEvaluator.TrialConversions([], [old], [T("DISNEY", Visa, "2026-04-15", -1_099)]).ShouldBeEmpty(); // 104 days

        AlertEvaluator.TrialConversions([], [T("APPLE", Visa, "2026-05-01", 0)], [T("APPLE", Visa, "2026-05-08", -99)]).ShouldBeEmpty();

        // A refund between the trial and the charge is ignored.
        var refund = T("PEACOCK", Visa, "2026-05-10", 500);
        AlertEvaluator.TrialConversions([], [T("PEACOCK", Visa, "2026-05-01", 0), refund], [T("PEACOCK", Visa, "2026-05-31", -599)])
            .ShouldHaveSingleItem();
    }

    // Idempotence -------------------------------------------------------------------------------

    [Fact]
    public void Evaluating_again_with_the_stored_keys_proposes_nothing()
    {
        var netflix = Item("NETFLIX", Visa, -1_549, "2026-05-12", "2026-04-12", id: 1);
        var rent = Item("RENT", Checking, -185_000, "2026-05-01", "2026-04-01", id: 2);
        var history = new[] { T("NETFLIX", Visa, "2026-04-12", -1_549), T("HULU", Visa, "2026-04-20", 0) };
        var batch = new[] { T("NETFLIX", Visa, "2026-05-12", -1_799), T("HULU", Visa, "2026-05-20", -1_499) };
        var detections = RecurringDetector.Detect(RealisticLedger.Transactions(), RealisticLedger.AsOf);
        var decisions = RecurringReconciler.Reconcile(detections, [], newId: () => RecurringSeries.Id(23, Interlocked.Increment(ref _n)));

        var first = AlertEvaluator.Evaluate(Input("2026-05-25", [netflix, rent], history, batch, decisions));
        first.Select(a => a.Kind).Distinct().Order().ShouldBe([AlertKind.PriceIncrease, AlertKind.MissingExpected, AlertKind.NewRecurring, AlertKind.TrialConversion]);
        first.Select(a => a.Key).ShouldBeUnique();

        // Store them as entities, read the keys back from the stored JSON, and run again.
        var stored = first.Select(p => p.ToEntity(new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc))).ToList();
        var keys = stored.Select(a => AlertKeys.Of(a)!).ToHashSet();
        keys.SetEquals(first.Select(a => a.Key)).ShouldBeTrue();

        AlertEvaluator.Evaluate(Input("2026-05-25", [netflix, rent], history, batch, decisions, keys)).ShouldBeEmpty();

        // The batch re-imported as history (and again as a batch) still proposes nothing new.
        AlertEvaluator.Evaluate(Input("2026-05-26", [netflix, rent], [.. history, .. batch], batch, decisions, keys)).ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_item_alerts_once_per_expected_date()
    {
        var rent = Item("RENT", Checking, -185_000, "2026-10-01", "2026-09-01");
        var first = AlertEvaluator.Evaluate(Input("2026-10-04", [rent]));
        var keys = first.Select(a => a.Key).ToHashSet();
        AlertEvaluator.Evaluate(Input("2026-10-09", [rent], keys: keys)).ShouldBeEmpty();

        // Next month's miss is a different occurrence.
        var november = rent with { NextExpectedDate = D("2026-11-01"), LastSeenDate = D("2026-10-12") };
        AlertEvaluator.Evaluate(Input("2026-11-05", [november], keys: keys)).ShouldHaveSingleItem();
    }

    // Entities and payloads ---------------------------------------------------------------------

    [Fact]
    public void Proposals_become_alert_entities_with_a_json_payload()
    {
        var proposal = new AlertProposal(
            AlertKind.PriceIncrease,
            "PriceIncrease:k",
            RecurringSeries.Id(21, 1),
            RecurringSeries.Id(20, 1),
            new AlertPayload("PriceIncrease:k", Payee: "NETFLIX", Date: D("2026-05-12"), Amount: -1_799, PreviousAmount: -1_549, IncreaseBasisPoints: 1_613));
        var created = new DateTime(2026, 5, 13, 8, 0, 0, DateTimeKind.Utc);

        var alert = proposal.ToEntity(created, RecurringSeries.Id(24, 1));

        alert.Id.ShouldBe(RecurringSeries.Id(24, 1));
        alert.Kind.ShouldBe(AlertKind.PriceIncrease);
        alert.RecurringItemId.ShouldBe(proposal.RecurringItemId);
        alert.TransactionId.ShouldBe(proposal.TransactionId);
        alert.CreatedAt.ShouldBe(created);
        alert.ReadAt.ShouldBeNull();
        alert.DismissedAt.ShouldBeNull();
        alert.PayloadJson.ShouldBe("""{"key":"PriceIncrease:k","payee":"NETFLIX","date":"2026-05-12","amount":-1799,"previousAmount":-1549,"increaseBasisPoints":1613}""");
        AlertPayload.FromJson(alert.PayloadJson).ShouldBe(proposal.Payload);
        proposal.ToEntity(created).Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void Payload_json_round_trips_enums_and_rejects_foreign_json()
    {
        var payload = new AlertPayload("NewRecurring:x", Cadence: RecurrenceCadence.Quarterly, NextExpectedDate: D("2026-10-15"), TrialTransactionId: RecurringSeries.Id(20, 9));
        payload.ToJson().ShouldContain("\"cadence\":\"Quarterly\"");
        AlertPayload.FromJson(payload.ToJson()).ShouldBe(payload);
        AlertPayload.FromJson("{}").ShouldBeNull();
        AlertPayload.FromJson("not json").ShouldBeNull();
        AlertPayload.FromJson(null).ShouldBeNull();
        AlertKeys.Of(new Alert()).ShouldBeNull();
    }

    [Fact]
    public void Alert_item_from_entity()
    {
        var entity = new RecurringItem
        {
            Id = RecurringSeries.Id(21, 5),
            AccountId = Visa,
            Cadence = RecurrenceCadence.Monthly,
            ExpectedAmount = -1_549,
            IsVariableAmount = true,
            NextExpectedDate = D("2026-10-12"),
            LastSeenDate = D("2026-09-12"),
            Status = RecurringStatus.Active,
        };
        AlertRecurringItem.From(entity, "NETFLIX").ShouldBe(
            new AlertRecurringItem(entity.Id, "NETFLIX", Visa, RecurrenceCadence.Monthly, -1_549, true, D("2026-10-12"), D("2026-09-12"), RecurringStatus.Active));
    }
}
