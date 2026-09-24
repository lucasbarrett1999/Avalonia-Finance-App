using System.Globalization;
using Keel.Domain.Entities;
using Keel.Domain.Forecast;
using Keel.Domain.Scheduling;
using Keel.Domain.Tests.Recurring;

namespace Keel.Domain.Tests.Forecast;

public class ForecastEngineTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);
    private static readonly Guid CheckingId = RecurringSeries.Account(1);
    private static readonly Guid SavingsId = RecurringSeries.Account(3);
    private static readonly Guid VisaId = RecurringSeries.Account(2);
    private static readonly Guid RentPayee = RecurringSeries.Id(30, 1);
    private static readonly Guid EmployerPayee = RecurringSeries.Id(30, 2);
    private static readonly Guid TransferPayee = RecurringSeries.Id(30, 3);
    private static readonly Guid GroceryPayee = RecurringSeries.Id(30, 4);

    private static readonly ForecastAccount Checking = new(CheckingId, "Checking", AccountType.Checking, true, 300_000);
    private static readonly ForecastAccount Savings = new(SavingsId, "Savings", AccountType.Savings, true, 1_000_000);
    private static readonly ForecastAccount Visa = new(VisaId, "Visa", AccountType.CreditCard, true, -50_000);

    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ForecastScheduled Sched(int id, Guid account, Guid payee, long amount, string rule, string next, Guid? transfer = null, string? end = null) =>
        new(RecurringSeries.Id(31, id), account, payee, amount, RecurrenceRule.Parse(rule), D(next), end is null ? null : D(end), transfer, null, $"S{id}");

    private static ForecastRecurring Rec(int id, Guid? account, Guid payee, long amount, RecurrenceCadence cadence, string next, string lastSeen, RecurringStatus status = RecurringStatus.Active, Guid? scheduledId = null) =>
        new(RecurringSeries.Id(32, id), payee, account, cadence, amount, D(next), D(lastSeen), status, scheduledId, null, $"R{id}");

    private static ForecastInput Input(
        IReadOnlyList<ForecastAccount>? accounts = null,
        IReadOnlyList<ForecastScheduled>? scheduled = null,
        IReadOnlyList<ForecastRecurring>? recurring = null,
        IReadOnlyList<ForecastHistoryTransaction>? history = null) =>
        new(Today, accounts ?? [Checking], scheduled ?? [], recurring ?? [], history ?? []);

    [Fact]
    public void Without_sources_the_balance_is_flat_for_ninety_one_days()
    {
        var result = ForecastEngine.Compute(Input());

        result.Start.ShouldBe(Today);
        result.End.ShouldBe(D("2026-12-23"));
        var series = result.Series(CheckingId);
        series.Days.Count.ShouldBe(91);
        series.Days.ShouldAllBe(d => d.Balance == 300_000 && d.Change == 0);
        series.Lowest.Date.ShouldBe(Today);
        series.BelowFloor.ShouldBeEmpty();
        result.Combined.Days.Count.ShouldBe(91);
        result.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Only_open_on_budget_cash_accounts_are_projected()
    {
        var closed = new ForecastAccount(RecurringSeries.Account(4), "Old", AccountType.Checking, true, 1_000, IsClosed: true);
        var tracking = new ForecastAccount(RecurringSeries.Account(5), "Brokerage", AccountType.Investment, false, 5_000_000);
        var offBudgetSavings = new ForecastAccount(RecurringSeries.Account(6), "Emergency", AccountType.Savings, false, 800_000);
        var cash = new ForecastAccount(RecurringSeries.Account(7), "Wallet", AccountType.Cash, true, 4_000);

        var result = ForecastEngine.Compute(Input([Checking, Visa, closed, tracking, offBudgetSavings, cash, Savings]));

        result.Accounts.Select(a => a.Name).ShouldBe(["Checking", "Wallet", "Savings"]);
        result.Combined.StartingBalance.ShouldBe(300_000 + 4_000 + 1_000_000);
        result.Combined.Name.ShouldBe(ForecastEngine.CombinedName);
        Should.Throw<ArgumentException>(() => result.Series(VisaId));
    }

    [Fact]
    public void Scheduled_and_recurring_occurrences_move_the_balance()
    {
        var rent = Sched(1, CheckingId, RentPayee, -185_000, "FREQ=MONTHLY;BYMONTHDAY=1", "2026-10-01");
        var pay = Rec(1, CheckingId, EmployerPayee, 245_000, RecurrenceCadence.Biweekly, "2026-10-02", "2026-09-18");

        var result = ForecastEngine.Compute(Input(scheduled: [rent], recurring: [pay]), new ForecastOptions(Days: 30));
        var s = result.Series(CheckingId);

        s.Days.Count.ShouldBe(31);
        s.Days.Single(d => d.Date == D("2026-10-01")).ShouldBe(new ForecastDay(D("2026-10-01"), 300_000, -185_000, 115_000));
        s.Days.Single(d => d.Date == D("2026-10-02")).Balance.ShouldBe(360_000);
        s.Days.Single(d => d.Date == D("2026-10-16")).Balance.ShouldBe(605_000);
        s.Days[^1].Date.ShouldBe(D("2026-10-24"));
        s.Days[^1].Balance.ShouldBe(605_000);
        s.Lowest.ShouldBe(new ForecastDay(D("2026-10-01"), 300_000, -185_000, 115_000));
        result.Entries.Select(e => (e.Date, e.Kind, e.Amount)).ShouldBe(
        [
            (D("2026-10-01"), ForecastEntryKind.Scheduled, -185_000L),
            (D("2026-10-02"), ForecastEntryKind.Recurring, 245_000L),
            (D("2026-10-16"), ForecastEntryKind.Recurring, 245_000L),
        ]);
    }

    [Fact]
    public void Explain_lists_what_lands_on_a_day()
    {
        var rent = Sched(1, CheckingId, RentPayee, -185_000, "FREQ=MONTHLY;BYMONTHDAY=1", "2026-10-01");
        var transfer = Sched(2, CheckingId, TransferPayee, -50_000, "FREQ=MONTHLY;BYMONTHDAY=1", "2026-10-01", transfer: SavingsId);
        var result = ForecastEngine.Compute(Input([Checking, Savings], [rent, transfer]));

        var checking = result.Explain(D("2026-10-01"), CheckingId);
        checking.Opening.ShouldBe(300_000);
        checking.Entries.Select(e => e.Label).ShouldBe(["S1", "S2"]);
        checking.Closing.ShouldBe(65_000);
        (checking.Opening + checking.Entries.Sum(e => e.Amount)).ShouldBe(checking.Closing);

        var all = result.Explain(D("2026-10-01"));
        all.AccountId.ShouldBeNull();
        all.Entries.Count.ShouldBe(3);
        all.Opening.ShouldBe(1_300_000);
        all.Closing.ShouldBe(1_115_000);

        result.Explain(D("2026-10-02"), CheckingId).Entries.ShouldBeEmpty();
        Should.Throw<ArgumentOutOfRangeException>(() => result.Explain(Today.AddDays(-1)));
        Should.Throw<ArgumentOutOfRangeException>(() => result.Explain(result.End.AddDays(1)));
    }

    [Fact]
    public void A_scheduled_transfer_moves_money_between_accounts_and_nets_to_zero_combined()
    {
        var transfer = Sched(1, CheckingId, TransferPayee, -50_000, "FREQ=MONTHLY;BYMONTHDAY=5", "2026-10-05", transfer: SavingsId);
        var result = ForecastEngine.Compute(Input([Checking, Savings], [transfer]));

        result.Series(CheckingId).Days[^1].Balance.ShouldBe(300_000 - (3 * 50_000));
        result.Series(SavingsId).Days[^1].Balance.ShouldBe(1_000_000 + (3 * 50_000));
        result.Combined.Days.ShouldAllBe(d => d.Balance == 1_300_000);
        var savingsSide = result.Entries.First(e => e.AccountId == SavingsId);
        savingsSide.Kind.ShouldBe(ForecastEntryKind.ScheduledTransfer);
        savingsSide.Amount.ShouldBe(50_000);
        savingsSide.CounterpartAccountId.ShouldBe(CheckingId);
    }

    [Fact]
    public void A_card_payment_from_checking_affects_checking_only()
    {
        var payment = Sched(1, CheckingId, TransferPayee, -40_000, "FREQ=MONTHLY;BYMONTHDAY=20", "2026-10-20", transfer: VisaId);
        var fromCard = Sched(2, VisaId, TransferPayee, 40_000, "FREQ=MONTHLY;BYMONTHDAY=25", "2026-10-25", transfer: CheckingId);
        var cardOnly = Sched(3, VisaId, RentPayee, -1_000, "FREQ=MONTHLY;BYMONTHDAY=25", "2026-10-25");

        var result = ForecastEngine.Compute(Input([Checking, Visa], [payment, fromCard, cardOnly]));

        result.Entries.ShouldAllBe(e => e.AccountId == CheckingId);
        result.Entries.Count(e => e.SourceId == fromCard.Id).ShouldBe(2); // recorded on the card: the checking side
        result.Entries.Where(e => e.SourceId == fromCard.Id).ShouldAllBe(e => e.Kind == ForecastEntryKind.ScheduledTransfer && e.Amount == -40_000);
        result.Skipped.ShouldHaveSingleItem().ShouldBe(new ForecastSkip(cardOnly.Id, ForecastSourceKind.Scheduled, ForecastSkipReason.NotCashAccount, "S3"));
    }

    [Fact]
    public void Double_count_guard_skips_a_recurring_item_covered_by_a_schedule()
    {
        var scheduledRent = Sched(1, CheckingId, RentPayee, -185_000, "FREQ=MONTHLY;BYMONTHDAY=1", "2026-10-01");
        var recurringRent = Rec(1, CheckingId, RentPayee, -185_000, RecurrenceCadence.Monthly, "2026-10-01", "2026-09-01");

        var guarded = ForecastEngine.Compute(Input(scheduled: [scheduledRent], recurring: [recurringRent]));
        guarded.Entries.Count(e => e.Date == D("2026-10-01")).ShouldBe(1);
        guarded.Entries.Single(e => e.Date == D("2026-10-01")).Kind.ShouldBe(ForecastEntryKind.Scheduled);
        guarded.Skipped.ShouldHaveSingleItem().Reason.ShouldBe(ForecastSkipReason.CoveredBySchedule);
        guarded.Series(CheckingId).Days[^1].Balance.ShouldBe(300_000 - (3 * 185_000));

        // Without the schedule the recurring item counts, with the same result.
        var unguarded = ForecastEngine.Compute(Input(recurring: [recurringRent]));
        unguarded.Series(CheckingId).Days[^1].Balance.ShouldBe(guarded.Series(CheckingId).Days[^1].Balance);
        unguarded.Skipped.ShouldBeEmpty();

        // A different payee is not a duplicate.
        var other = recurringRent with { PayeeId = GroceryPayee };
        ForecastEngine.Compute(Input(scheduled: [scheduledRent], recurring: [other])).Entries.Count(e => e.Date == D("2026-10-01")).ShouldBe(2);

        // The explicit link counts even when the scheduled transaction uses another payee.
        var linked = other with { ScheduledTransactionId = scheduledRent.Id };
        ForecastEngine.Compute(Input(scheduled: [scheduledRent], recurring: [linked])).Skipped.ShouldHaveSingleItem().Reason.ShouldBe(ForecastSkipReason.CoveredBySchedule);
    }

    [Theory]
    [InlineData(RecurringStatus.Detected, ForecastSkipReason.NotConfirmed)]
    [InlineData(RecurringStatus.Paused, ForecastSkipReason.NotConfirmed)]
    [InlineData(RecurringStatus.Ended, ForecastSkipReason.NotConfirmed)]
    [InlineData(RecurringStatus.Dismissed, ForecastSkipReason.NotConfirmed)]
    public void Only_confirmed_recurring_items_count(RecurringStatus status, ForecastSkipReason reason)
    {
        var item = Rec(1, CheckingId, RentPayee, -185_000, RecurrenceCadence.Monthly, "2026-10-01", "2026-09-01", status);
        var result = ForecastEngine.Compute(Input(recurring: [item]));
        result.Entries.ShouldBeEmpty();
        result.Skipped.ShouldHaveSingleItem().ShouldBe(new ForecastSkip(item.Id, ForecastSourceKind.Recurring, reason, "R1"));
    }

    [Fact]
    public void Recurring_items_without_an_account_or_on_a_card_are_skipped()
    {
        var noAccount = Rec(1, null, RentPayee, -1_000, RecurrenceCadence.Monthly, "2026-10-01", "2026-09-01");
        var onCard = Rec(2, VisaId, RentPayee, -1_799, RecurrenceCadence.Monthly, "2026-10-12", "2026-09-12");
        var result = ForecastEngine.Compute(Input([Checking, Visa], recurring: [noAccount, onCard]));
        result.Skipped.Select(s => s.Reason).ShouldBe([ForecastSkipReason.NoAccount, ForecastSkipReason.NotCashAccount]);
    }

    [Fact]
    public void Overdue_occurrences_within_the_window_land_on_day_zero()
    {
        // Not yet entered: due 5 days ago (counts today) and 20 days ago (too old, ignored).
        var recent = Sched(1, CheckingId, RentPayee, -10_000, "FREQ=MONTHLY;BYMONTHDAY=19", "2026-09-19");
        var stale = Sched(2, CheckingId, GroceryPayee, -20_000, "FREQ=MONTHLY;BYMONTHDAY=4", "2026-09-04");
        var late = Rec(1, CheckingId, EmployerPayee, 245_000, RecurrenceCadence.Biweekly, "2026-09-18", "2026-09-04");

        var result = ForecastEngine.Compute(Input(scheduled: [recent, stale], recurring: [late]), new ForecastOptions(Days: 20));

        var today = result.Explain(Today, CheckingId);
        today.Entries.Select(e => (e.Label, e.Amount, e.IsOverdue, e.DueDate)).ShouldBe(
        [
            ("S1", -10_000L, true, D("2026-09-19")),
            ("R1", 245_000L, true, D("2026-09-18")),
        ]);
        today.Closing.ShouldBe(300_000 - 10_000 + 245_000);
        result.Entries.Where(e => e.SourceId == stale.Id).Select(e => e.Date).ShouldBe([D("2026-10-04")]);
        result.Entries.Where(e => e.SourceId == late.Id).Select(e => e.Date).ShouldBe([Today, D("2026-10-02")]);

        ForecastEngine.Compute(Input(scheduled: [recent]), new ForecastOptions(OverdueWindowDays: 4)).Explain(Today).Entries.ShouldBeEmpty();
    }

    [Fact]
    public void End_date_until_and_count_limit_occurrences()
    {
        var withEnd = Sched(1, CheckingId, RentPayee, -1_000, "FREQ=MONTHLY;BYMONTHDAY=10", "2026-10-10", end: "2026-11-10");
        var withUntil = Sched(2, CheckingId, RentPayee, -1_000, "FREQ=WEEKLY;BYDAY=MO;UNTIL=20261012", "2026-09-28");
        var withCount = Sched(3, CheckingId, RentPayee, -1_000, "FREQ=DAILY;COUNT=3", "2026-09-30");

        var result = ForecastEngine.Compute(Input(scheduled: [withEnd, withUntil, withCount]));

        result.Entries.Where(e => e.SourceId == withEnd.Id).Select(e => e.Date).ShouldBe([D("2026-10-10"), D("2026-11-10")]);
        result.Entries.Where(e => e.SourceId == withUntil.Id).Select(e => e.Date).ShouldBe([D("2026-09-28"), D("2026-10-05"), D("2026-10-12")]);
        result.Entries.Where(e => e.SourceId == withCount.Id).Select(e => e.Date).ShouldBe([D("2026-09-30"), D("2026-10-01"), D("2026-10-02")]);
    }

    [Fact]
    public void Recurring_items_project_with_their_inferred_rule_including_month_ends()
    {
        var loan = Rec(1, CheckingId, RentPayee, -30_000, RecurrenceCadence.Monthly, "2026-09-30", "2026-08-31");
        var twice = Rec(2, CheckingId, EmployerPayee, 180_000, RecurrenceCadence.Semimonthly, "2026-09-30", "2026-09-15");
        var result = ForecastEngine.Compute(Input(recurring: [loan, twice]), new ForecastOptions(Days: 70));

        result.Entries.Where(e => e.SourceId == loan.Id).Select(e => e.Date).ShouldBe([D("2026-09-30"), D("2026-10-31"), D("2026-11-30")]);
        result.Entries.Where(e => e.SourceId == twice.Id).Select(e => e.Date).ShouldBe(
            [D("2026-09-30"), D("2026-10-15"), D("2026-10-31"), D("2026-11-15"), D("2026-11-30")]);

        var explicitRule = loan with { Rule = RecurrenceRule.MonthlyOnDay(15), NextExpectedDate = D("2026-10-15") };
        ForecastEngine.Compute(Input(recurring: [explicitRule]), new ForecastOptions(Days: 70)).Entries.Select(e => e.Date)
            .ShouldBe([D("2026-10-15"), D("2026-11-15")]);
    }

    [Fact]
    public void Floor_lists_every_day_below_it_and_the_lowest_day_is_the_first_minimum()
    {
        var rent = Sched(1, CheckingId, RentPayee, -250_000, "FREQ=MONTHLY;BYMONTHDAY=1", "2026-10-01");
        var pay = Sched(2, CheckingId, EmployerPayee, 250_000, "FREQ=MONTHLY;BYMONTHDAY=10", "2026-10-10");
        var result = ForecastEngine.Compute(Input([Checking, Savings], [rent, pay]), new ForecastOptions(Days: 30, Floor: 100_000));

        var s = result.Series(CheckingId);
        s.Lowest.Date.ShouldBe(D("2026-10-01"));
        s.Lowest.Balance.ShouldBe(50_000);
        s.BelowFloor.Select(d => d.Date).ShouldBe(Enumerable.Range(0, 9).Select(i => D("2026-10-01").AddDays(i)));
        s.BelowFloor.ShouldAllBe(d => d.Balance == 50_000);
        result.Combined.BelowFloor.ShouldBeEmpty();
        result.Floor.ShouldBe(100_000);

        // A floor equal to the balance is not "below".
        ForecastEngine.Compute(Input([Checking], [rent, pay]), new ForecastOptions(Floor: 50_000)).Series(CheckingId).BelowFloor.ShouldBeEmpty();
    }

    [Fact]
    public void Discretionary_spend_averages_non_recurring_outflows_of_the_last_ninety_days()
    {
        var rentSchedule = Sched(1, CheckingId, RentPayee, -185_000, "FREQ=MONTHLY;BYMONTHDAY=1", "2026-10-01");
        var paycheck = Rec(1, CheckingId, EmployerPayee, 245_000, RecurrenceCadence.Biweekly, "2026-10-02", "2026-09-18");
        var dismissed = Rec(2, CheckingId, TransferPayee, -9_999, RecurrenceCadence.Monthly, "2026-10-05", "2026-09-05", RecurringStatus.Dismissed);
        var n = 0;
        ForecastHistoryTransaction H(string date, long amount, Guid? payee, bool transfer = false, Guid? scheduledFrom = null, Guid? account = null) =>
            new(RecurringSeries.Id(33, ++n), account ?? CheckingId, D(date), amount, payee, transfer, scheduledFrom);
        var history = new[]
        {
            H("2026-09-23", -9_000, GroceryPayee),                  // counted (yesterday)
            H("2026-06-26", -9_000, GroceryPayee),                  // counted (first day of the window)
            H("2026-06-25", -9_000, GroceryPayee),                  // outside the window
            H("2026-09-24", -9_000, GroceryPayee),                  // today: outside the window
            H("2026-08-10", -4_500, TransferPayee),                 // counted: the recurring item is dismissed
            H("2026-09-01", -185_000, RentPayee),                   // scheduled payee
            H("2026-08-01", -185_000, null, scheduledFrom: rentSchedule.Id),
            H("2026-08-15", -50_000, null, transfer: true),         // transfer
            H("2026-09-18", 245_000, EmployerPayee),                // inflow
            H("2026-09-04", -2_000, EmployerPayee),                 // recurring payee (paycheck item)
            H("2026-09-10", -7_700, GroceryPayee, account: SavingsId),
        };

        var input = Input([Checking, Savings], [rentSchedule], [paycheck, dismissed], history);
        var spend = ForecastEngine.ComputeDiscretionary(Checking, input);
        spend.ShouldBe(new DiscretionarySpend(CheckingId, D("2026-06-26"), D("2026-09-23"), 90, 22_500, 3, 4, 250));

        var result = ForecastEngine.Compute(input, new ForecastOptions(Days: 10, IncludeDiscretionarySpend: true));
        result.Discretionary.Select(d => (d.AccountId, d.Daily)).ShouldBe([(CheckingId, 250L), (SavingsId, 86L)]);
        var checkingSeries = result.Series(CheckingId);
        checkingSeries.DailyDiscretionarySpend.ShouldBe(250);
        checkingSeries.Days[0].Change.ShouldBe(0);                                   // not on day 0
        checkingSeries.Days[1].Change.ShouldBe(-250);
        result.Explain(D("2026-09-25"), CheckingId).Entries.ShouldHaveSingleItem().Kind.ShouldBe(ForecastEntryKind.Discretionary);
        result.Combined.DailyDiscretionarySpend.ShouldBe(336);

        // Off by default.
        ForecastEngine.Compute(input).Discretionary.ShouldBeEmpty();
        ForecastEngine.Compute(input).Entries.ShouldNotContain(e => e.Kind == ForecastEntryKind.Discretionary);
    }

    [Fact]
    public void Discretionary_window_starts_at_the_opening_date_of_a_new_account()
    {
        var fresh = Checking with { OpeningDate = D("2026-09-14") };
        var history = new[] { new ForecastHistoryTransaction(RecurringSeries.Id(33, 1), CheckingId, D("2026-09-20"), -10_000, GroceryPayee, false) };
        var spend = ForecastEngine.ComputeDiscretionary(fresh, Input([fresh], history: history));
        spend.From.ShouldBe(D("2026-09-14"));
        spend.Days.ShouldBe(10);
        spend.Daily.ShouldBe(1_000);

        var openedToday = Checking with { OpeningDate = Today };
        ForecastEngine.ComputeDiscretionary(openedToday, Input([openedToday])).Days.ShouldBe(1);
    }

    [Fact]
    public void Zero_days_gives_only_today()
    {
        var result = ForecastEngine.Compute(Input(), new ForecastOptions(Days: 0, IncludeDiscretionarySpend: true));
        result.Series(CheckingId).Days.ShouldHaveSingleItem().Date.ShouldBe(Today);
        Should.Throw<ArgumentOutOfRangeException>(() => ForecastEngine.Compute(Input(), new ForecastOptions(Days: -1)));
        Should.Throw<ArgumentOutOfRangeException>(() => ForecastEngine.Compute(Input(), new ForecastOptions(DiscretionaryLookbackDays: 0)));
    }

    [Fact]
    public void Series_and_combined_totals_are_consistent()
    {
        var input = ForecastScenario.Build();
        var result = ForecastEngine.Compute(input, ForecastScenario.Options);
        foreach (var series in result.Accounts)
        {
            var total = result.Entries.Where(e => e.AccountId == series.AccountId).Sum(e => e.Amount);
            series.Days[^1].Balance.ShouldBe(series.StartingBalance + total);
            for (var i = 1; i < series.Days.Count; i++)
            {
                series.Days[i].Opening.ShouldBe(series.Days[i - 1].Balance);
            }
        }

        for (var i = 0; i < result.Combined.Days.Count; i++)
        {
            result.Combined.Days[i].Balance.ShouldBe(result.Accounts.Sum(a => a.Days[i].Balance));
        }
    }

    [Fact]
    public void Inputs_from_entities()
    {
        var scheduled = new ScheduledTransaction
        {
            AccountId = CheckingId,
            Amount = -185_000,
            PayeeId = RentPayee,
            RecurrenceRule = "FREQ=MONTHLY;BYMONTHDAY=1",
            NextDate = D("2026-10-01"),
            EndDate = D("2027-06-01"),
            TransferAccountId = SavingsId,
        };
        var fs = ForecastScheduled.From(scheduled, "Rent");
        fs.ShouldBe(new ForecastScheduled(scheduled.Id, CheckingId, RentPayee, -185_000, RecurrenceRule.MonthlyOnDay(1), D("2026-10-01"), D("2027-06-01"), SavingsId, null, "Rent"));
        Should.Throw<RecurrenceRuleFormatException>(() => ForecastScheduled.From(new ScheduledTransaction { RecurrenceRule = "FREQ=HOURLY" }));

        var item = new RecurringItem
        {
            PayeeId = RentPayee,
            AccountId = CheckingId,
            Cadence = RecurrenceCadence.Monthly,
            ExpectedAmount = -185_000,
            NextExpectedDate = D("2026-10-01"),
            LastSeenDate = D("2026-09-01"),
            Status = RecurringStatus.Active,
            ScheduledTransactionId = scheduled.Id,
        };
        ForecastRecurring.From(item, "Rent").ShouldBe(new ForecastRecurring(item.Id, RentPayee, CheckingId, RecurrenceCadence.Monthly, -185_000, D("2026-10-01"), D("2026-09-01"), RecurringStatus.Active, scheduled.Id, null, "Rent"));
    }
}
