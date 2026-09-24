using System.Globalization;
using Keel.Domain.Recurring;
using Xunit.Abstractions;

namespace Keel.Domain.Tests.Recurring;

public class RecurringDetectorTests(ITestOutputHelper output)
{
    private static readonly DateOnly AsOf = new(2026, 9, 24);
    private static readonly Guid Checking = RecurringSeries.Account(1);
    private static readonly Guid Visa = RecurringSeries.Account(2);

    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static List<RecurringTransaction> Series(RecurrenceCadence cadence, string first, int count, long amount, (int, int)? semimonthly = null) =>
        RecurringSeries.Build("PAYEE", Checking, RecurringSeries.Dates(cadence, D(first), count, semimonthly), amount);

    private static List<RecurringTransaction> On(string payee, Guid account, long amount, params string[] dates) =>
        RecurringSeries.Build(payee, account, dates.Select(D), amount, ids: n => RecurringSeries.Id(3, n), idOffset: payee.GetHashCode(StringComparison.Ordinal) & 0xFFFF);

    // One exact series per cadence ----------------------------------------------------------------

    [Theory]
    [InlineData(RecurrenceCadence.Weekly, "2026-06-05", 16, -2_500L, "2026-09-18", "2026-09-25", "FREQ=WEEKLY;BYDAY=FR")]
    [InlineData(RecurrenceCadence.Biweekly, "2025-10-03", 26, 245_000L, "2026-09-18", "2026-10-02", "FREQ=WEEKLY;INTERVAL=2;BYDAY=FR")]
    [InlineData(RecurrenceCadence.Monthly, "2025-07-12", 15, -1_549L, "2026-09-12", "2026-10-12", "FREQ=MONTHLY;BYMONTHDAY=12")]
    [InlineData(RecurrenceCadence.Monthly, "2025-07-31", 14, -9_900L, "2026-08-31", "2026-09-30", "FREQ=MONTHLY;BYMONTHDAY=-1")]
    [InlineData(RecurrenceCadence.Quarterly, "2025-07-15", 5, -31_240L, "2026-07-15", "2026-10-15", "FREQ=MONTHLY;INTERVAL=3;BYMONTHDAY=15")]
    [InlineData(RecurrenceCadence.Yearly, "2025-08-03", 2, -1_498L, "2026-08-03", "2027-08-03", "FREQ=YEARLY;BYMONTH=8;BYMONTHDAY=3")]
    public void Detects_each_cadence(RecurrenceCadence cadence, string first, int count, long amount, string last, string next, string rule)
    {
        var items = RecurringDetector.Detect(Series(cadence, first, count, amount), AsOf);

        var item = items.ShouldHaveSingleItem();
        item.Cadence.ShouldBe(cadence);
        item.ExpectedAmount.ShouldBe(amount);
        item.AmountTolerance.ShouldBe(Math.Max(200, Math.Abs(amount) / 10));
        item.IsVariableAmount.ShouldBeFalse();
        item.Confidence.ShouldBe(1.0);
        item.CadenceFraction.ShouldBe(1.0);
        item.OccurrenceCount.ShouldBe(count);
        item.LastSeenDate.ShouldBe(D(last));
        item.FirstSeenDate.ShouldBe(D(first));
        item.NextExpectedDate.ShouldBe(D(next));
        item.Rule.ToString().ShouldBe(rule);
        item.IsLapsed.ShouldBeFalse();
        item.TransactionIds.Count.ShouldBe(count);
        item.Scores.Select(s => s.Cadence).ShouldBe(Enum.GetValues<RecurrenceCadence>());
        item.IsInflow.ShouldBe(amount > 0);
    }

    [Theory]
    [InlineData(1, 15, "2026-01-01", 17, "2026-09-01", "2026-09-15", "FREQ=MONTHLY;BYMONTHDAY=1,15")]
    [InlineData(15, -1, "2026-01-15", 16, "2026-08-31", "2026-09-15", "FREQ=MONTHLY;BYMONTHDAY=15,-1")]
    [InlineData(5, 20, "2025-12-05", 19, "2026-09-05", "2026-09-20", "FREQ=MONTHLY;BYMONTHDAY=5,20")]
    public void Detects_twice_monthly_rather_than_biweekly(int a, int b, string first, int count, string last, string next, string rule)
    {
        var item = RecurringDetector.Detect(Series(RecurrenceCadence.Semimonthly, first, count, 180_000, (a, b)), AsOf).ShouldHaveSingleItem();

        item.Cadence.ShouldBe(RecurrenceCadence.Semimonthly);
        item.LastSeenDate.ShouldBe(D(last));
        item.NextExpectedDate.ShouldBe(D(next));
        item.Rule.ToString().ShouldBe(rule);
        var biweekly = item.Scores.Single(s => s.Cadence == RecurrenceCadence.Biweekly);
        var semimonthly = item.Scores.Single(s => s.Cadence == RecurrenceCadence.Semimonthly);
        semimonthly.MeanResidualDays.ShouldBeLessThan(biweekly.MeanResidualDays);
    }

    [Fact]
    public void A_biweekly_series_is_not_mistaken_for_twice_monthly()
    {
        // Every gap is 14 days: inside both the biweekly and the semimonthly window.
        var item = RecurringDetector.Detect(Series(RecurrenceCadence.Biweekly, "2026-03-06", 14, 245_000), AsOf).ShouldHaveSingleItem();
        item.Cadence.ShouldBe(RecurrenceCadence.Biweekly);
        item.Scores.Single(s => s.Cadence == RecurrenceCadence.Semimonthly).Fraction.ShouldBe(1.0);
    }

    // Jitter and amount noise --------------------------------------------------------------------

    public static TheoryData<RecurrenceCadence, int> JitterCases()
    {
        var data = new TheoryData<RecurrenceCadence, int>();
        foreach (var cadence in Enum.GetValues<RecurrenceCadence>())
        {
            for (var seed = 1; seed <= 25; seed++)
            {
                data.Add(cadence, seed);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(JitterCases))]
    public void Detects_each_cadence_with_date_jitter_and_amount_noise(RecurrenceCadence cadence, int seed)
    {
        var random = new Random(seed);
        var window = CadenceWindow.For(cadence);
        var first = AsOf.AddMonths(-RecurringDetector.LookbackMonths).AddDays(window.ToleranceDays + random.Next(0, 20));
        var dates = RecurringSeries.Dates(cadence, first, 80, (1, 15)).Where(d => d <= AsOf.AddDays(-window.ToleranceDays)).ToList();
        var series = RecurringSeries.Build("PAYEE", Checking, dates, -4_000, random, window.ToleranceDays / 2, 0.04);

        var item = RecurringDetector.Detect(series, AsOf).ShouldHaveSingleItem();

        item.Cadence.ShouldBe(cadence);
        item.IsVariableAmount.ShouldBeFalse();
        item.Confidence.ShouldBeGreaterThanOrEqualTo(RecurringDetector.MinFraction);
        Math.Abs(item.ExpectedAmount + 4_000).ShouldBeLessThanOrEqualTo(160);
        item.IsLapsed.ShouldBeFalse();
    }

    // Amount: median, tolerance, variable --------------------------------------------------------

    [Fact]
    public void Expected_amount_is_the_median_of_the_last_six()
    {
        var dates = RecurringSeries.Dates(RecurrenceCadence.Monthly, D("2025-07-12"), 15).ToList();
        var amounts = new long[] { -999, -999, -999, -999, -999, -999, -999, -999, -999, -1_549, -1_549, -1_549, -1_799, -1_799, -1_799 };
        var series = dates.Select((d, i) => new RecurringTransaction(RecurringSeries.Id(1, i), Checking, d, amounts[i], "NETFLIX")).ToList();

        var item = RecurringDetector.Detect(series, AsOf).ShouldHaveSingleItem();

        item.ExpectedAmount.ShouldBe(-1_674);          // (-1549 + -1799) / 2
        item.AmountTolerance.ShouldBe(200);            // max($2, $1.67)
        item.IsVariableAmount.ShouldBeFalse();         // |-1799 - -1674| = 1.25 <= 2
        item.Confidence.ShouldBe(1.0);
    }

    [Fact]
    public void Utility_bills_with_varying_amounts_are_variable_and_less_confident()
    {
        var dates = RecurringSeries.Dates(RecurrenceCadence.Monthly, D("2025-07-20"), 14).ToList();
        var amounts = new long[] { -21_000, -19_800, -14_200, -9_500, -8_800, -11_400, -15_600, -17_900, -16_100, -12_300, -9_100, -8_500, -13_300, -19_400 };
        var series = dates.Select((d, i) => new RecurringTransaction(RecurringSeries.Id(1, i), Checking, d, amounts[i], "PGE")).ToList();

        var item = RecurringDetector.Detect(series, AsOf).ShouldHaveSingleItem();

        item.Cadence.ShouldBe(RecurrenceCadence.Monthly);
        item.IsVariableAmount.ShouldBeTrue();
        item.ExpectedAmount.ShouldBe(-12_800);         // median of the last six: -9100, -8500, -12300, -13300, -16100, -19400
        item.AmountTolerance.ShouldBe(1_280);
        item.Confidence.ShouldBe(0.8);
    }

    [Theory]
    [InlineData(new long[] { -500 }, -500)]
    [InlineData(new long[] { -500, -700 }, -600)]
    [InlineData(new long[] { -1, -2 }, -2)]           // -1.5 rounds half to even
    [InlineData(new long[] { -3, -2 }, -2)]           // -2.5 rounds half to even
    [InlineData(new long[] { 3, 1, 2 }, 2)]
    [InlineData(new long[] { 10, 40, 20, 30 }, 25)]
    public void Median(long[] values, long expected) => RecurringDetector.Median(values).ShouldBe(expected);

    [Theory]
    [InlineData(0, 200)]
    [InlineData(-1_999, 200)]
    [InlineData(-2_000, 200)]
    [InlineData(-2_005, 200)]
    [InlineData(-2_015, 202)]                          // 201.5 rounds half to even
    [InlineData(245_000, 24_500)]
    public void Tolerance_is_the_larger_of_two_dollars_and_ten_percent(long amount, long expected) =>
        RecurringDetector.Tolerance(amount).ShouldBe(expected);

    // Thresholds --------------------------------------------------------------------------------

    [Fact]
    public void Two_monthly_transactions_are_not_enough()
    {
        RecurringDetector.Detect(Series(RecurrenceCadence.Monthly, "2026-07-12", 2, -1_549), AsOf).ShouldBeEmpty();
        RecurringDetector.Detect(Series(RecurrenceCadence.Monthly, "2026-06-12", 3, -1_549), AsOf).ShouldHaveSingleItem();
    }

    [Fact]
    public void Two_yearly_transactions_are_enough_but_two_of_another_cadence_are_not()
    {
        RecurringDetector.Detect(On("DOMAIN", Checking, -1_498, "2025-08-03", "2026-08-05"), AsOf).ShouldHaveSingleItem().Cadence.ShouldBe(RecurrenceCadence.Yearly);
        RecurringDetector.Detect(On("DOMAIN", Checking, -1_498, "2025-08-03", "2026-02-05"), AsOf).ShouldBeEmpty();
        RecurringDetector.Detect(On("GYM", Checking, -1_498, "2026-08-03", "2026-08-10"), AsOf).ShouldBeEmpty();
    }

    [Fact]
    public void Fraction_below_seventy_percent_is_not_detected()
    {
        // 10 occurrences, 9 gaps: 6 monthly gaps (0.667) vs 7 monthly gaps (0.778).
        var failing = On("A", Checking, -1_000, "2025-10-01", "2025-11-01", "2025-12-01", "2026-01-01", "2026-02-01", "2026-03-01", "2026-03-10", "2026-03-20", "2026-04-20", "2026-04-29");
        RecurringDetector.Detect(failing, AsOf).ShouldBeEmpty();

        var passing = On("A", Checking, -1_000, "2025-10-01", "2025-11-01", "2025-12-01", "2026-01-01", "2026-02-01", "2026-03-01", "2026-04-01", "2026-04-10", "2026-04-20", "2026-05-20");
        var item = RecurringDetector.Detect(passing, AsOf).ShouldHaveSingleItem();
        item.Cadence.ShouldBe(RecurrenceCadence.Monthly);
        item.CadenceFraction.ShouldBe(7.0 / 9, 1e-12);
        item.Confidence.ShouldBe(7.0 / 9, 1e-12);
    }

    [Fact]
    public void Only_the_last_fifteen_months_up_to_the_as_of_date_count()
    {
        // Monthly from 2024-01 to 2026-12: the window is 2025-06-24 .. 2026-09-24.
        var series = Series(RecurrenceCadence.Monthly, "2024-01-12", 36, -1_549);
        var item = RecurringDetector.Detect(series, AsOf).ShouldHaveSingleItem();
        item.FirstSeenDate.ShouldBe(D("2025-07-12"));
        item.LastSeenDate.ShouldBe(D("2026-09-12"));
        item.OccurrenceCount.ShouldBe(15);

        // Three occurrences all older than the window: nothing.
        RecurringDetector.Detect(Series(RecurrenceCadence.Monthly, "2025-01-12", 3, -1_549), AsOf).ShouldBeEmpty();
    }

    [Fact]
    public void Groups_by_normalized_payee_and_account()
    {
        var dates = RecurringSeries.Dates(RecurrenceCadence.Monthly, D("2026-01-12"), 8).ToList();
        var raw = new[] { "NETFLIX", "PAYPAL *NETFLIX", "Netflix", "ACH DEBIT NETFLIX" };
        var onChecking = dates.Select((d, i) => RecurringTransaction.FromRaw(RecurringSeries.Id(1, i), Checking, d, -1_549, raw[i % raw.Length])).ToList();
        var onVisa = dates.Select((d, i) => RecurringTransaction.FromRaw(RecurringSeries.Id(2, i), Visa, d, -1_549, raw[i % raw.Length])).ToList();

        var items = RecurringDetector.Detect(onChecking.Concat(onVisa), AsOf);

        items.Count.ShouldBe(2);
        items.ShouldAllBe(i => i.NormalizedPayee == "NETFLIX");
        items.Select(i => i.AccountId).ShouldBe(new[] { Checking, Visa }.Order());
    }

    [Fact]
    public void Refunds_and_zero_amounts_do_not_break_a_pattern()
    {
        var series = Series(RecurrenceCadence.Monthly, "2025-12-12", 10, -1_549);
        series.Add(new RecurringTransaction(RecurringSeries.Id(9, 1), Checking, D("2026-03-20"), 1_549, "PAYEE"));
        series.Add(new RecurringTransaction(RecurringSeries.Id(9, 2), Checking, D("2026-05-02"), 0, "PAYEE"));

        var item = RecurringDetector.Detect(series, AsOf).ShouldHaveSingleItem();

        item.Cadence.ShouldBe(RecurrenceCadence.Monthly);
        item.OccurrenceCount.ShouldBe(10);
        item.Confidence.ShouldBe(1.0);
    }

    [Fact]
    public void Inflows_win_when_they_are_the_majority()
    {
        var series = Series(RecurrenceCadence.Biweekly, "2026-03-06", 14, 245_000);
        series.Add(new RecurringTransaction(RecurringSeries.Id(9, 1), Checking, D("2026-04-01"), -5_000, "PAYEE"));
        var item = RecurringDetector.Detect(series, AsOf).ShouldHaveSingleItem();
        item.ExpectedAmount.ShouldBe(245_000);
        item.IsInflow.ShouldBeTrue();
    }

    [Fact]
    public void Ignores_empty_payees_and_future_transactions()
    {
        var series = RecurringSeries.Build(string.Empty, Checking, RecurringSeries.Dates(RecurrenceCadence.Monthly, D("2026-01-12"), 8), -1_000);
        RecurringDetector.Detect(series, AsOf).ShouldBeEmpty();

        var future = Series(RecurrenceCadence.Monthly, "2026-09-25", 5, -1_000);
        RecurringDetector.Detect(future, AsOf).ShouldBeEmpty();
    }

    // Next expected date ------------------------------------------------------------------------

    [Fact]
    public void Monthly_next_date_keeps_the_day_of_month_after_an_early_payment()
    {
        // Rent due on the 1st, paid a day early for September.
        var item = RecurringDetector.Detect(On("RENT", Checking, -185_000, "2026-04-01", "2026-05-01", "2026-06-01", "2026-07-01", "2026-08-01", "2026-08-31"), AsOf)
            .ShouldHaveSingleItem();
        item.LastSeenDate.ShouldBe(D("2026-08-31"));
        item.NextExpectedDate.ShouldBe(D("2026-10-01"));
        item.Rule.ToString().ShouldBe("FREQ=MONTHLY;BYMONTHDAY=1");
    }

    [Fact]
    public void Monthly_next_date_after_a_late_payment()
    {
        var item = RecurringDetector.Detect(On("CARD", Checking, -5_000, "2026-04-15", "2026-05-15", "2026-06-15", "2026-07-15", "2026-08-15", "2026-09-18"), AsOf)
            .ShouldHaveSingleItem();
        item.NextExpectedDate.ShouldBe(D("2026-10-15"));
    }

    [Fact]
    public void Monthly_on_the_31st_is_expected_on_the_last_day_of_short_months()
    {
        var item = RecurringDetector.Detect(On("LOAN", Checking, -30_000, "2026-03-31", "2026-04-30", "2026-05-31", "2026-06-30", "2026-07-31", "2026-08-31"), AsOf)
            .ShouldHaveSingleItem();
        item.NextExpectedDate.ShouldBe(D("2026-09-30"));
        item.Rule.ToString().ShouldBe("FREQ=MONTHLY;BYMONTHDAY=-1");
    }

    [Fact]
    public void Monthly_on_the_30th_stays_on_the_30th_after_february()
    {
        var item = RecurringDetector.Detect(On("INS", Checking, -9_000, "2025-11-30", "2025-12-30", "2026-01-30", "2026-02-28"), D("2026-03-10"))
            .ShouldHaveSingleItem();
        item.NextExpectedDate.ShouldBe(D("2026-03-30"));
        item.Rule.ToString().ShouldBe("FREQ=MONTHLY;BYMONTHDAY=30");
    }

    [Fact]
    public void Weekly_next_date_returns_to_the_usual_weekday_after_a_holiday_shift()
    {
        // Paid on Fridays; the last one came a day early (Thursday).
        var item = RecurringDetector.Detect(On("PAY", Checking, 100_000, "2026-08-07", "2026-08-14", "2026-08-21", "2026-08-28", "2026-09-03"), D("2026-09-05"))
            .ShouldHaveSingleItem();
        item.Cadence.ShouldBe(RecurrenceCadence.Weekly);
        item.NextExpectedDate.ShouldBe(D("2026-09-11"));
        item.Rule.ToString().ShouldBe("FREQ=WEEKLY;BYDAY=FR");
    }

    [Fact]
    public void Yearly_next_date_from_a_leap_day()
    {
        var item = RecurringDetector.Detect(On("DUES", Checking, -12_000, "2024-02-29", "2025-02-28", "2026-02-28"), D("2026-03-01"))
            .ShouldHaveSingleItem();
        item.Cadence.ShouldBe(RecurrenceCadence.Yearly);
        item.NextExpectedDate.ShouldBe(D("2027-02-28"));
    }

    // Lapsed ------------------------------------------------------------------------------------

    [Fact]
    public void A_pattern_that_stopped_is_lapsed()
    {
        var gym = Series(RecurrenceCadence.Monthly, "2025-07-05", 8, -4_999); // last 2026-02-05
        var item = RecurringDetector.Detect(gym, AsOf).ShouldHaveSingleItem();
        item.NextExpectedDate.ShouldBe(D("2026-03-05"));
        item.IsLapsed.ShouldBeTrue();

        // One missed occurrence is not yet lapsed (the missing-item alert covers it).
        var late = Series(RecurrenceCadence.Monthly, "2025-09-05", 12, -4_999); // last 2026-08-05
        RecurringDetector.Detect(late, AsOf).ShouldHaveSingleItem().IsLapsed.ShouldBeFalse();
    }

    // Scores ------------------------------------------------------------------------------------

    [Fact]
    public void Scores_explain_every_candidate_cadence()
    {
        var scores = RecurringDetector.ScoreCadences([D("2026-01-01"), D("2026-01-08"), D("2026-01-15"), D("2026-02-15")]);
        scores.Select(s => (s.Cadence, s.Fraction)).ShouldBe(
        [
            (RecurrenceCadence.Weekly, 2.0 / 3),
            (RecurrenceCadence.Biweekly, 0),
            (RecurrenceCadence.Semimonthly, 0),
            (RecurrenceCadence.Monthly, 1.0 / 3),
            (RecurrenceCadence.Quarterly, 0),
            (RecurrenceCadence.Yearly, 0),
        ]);
        RecurringDetector.ScoreCadences([D("2026-01-01")]).ShouldAllBe(s => s.Fraction == 0);
    }

    [Fact]
    public void PickBest_prefers_the_highest_fraction()
    {
        RecurringDetector.PickBest([]).ShouldBeNull();
        RecurringDetector.PickBest(
        [
            new CadenceScore(RecurrenceCadence.Weekly, 0.5, 0),
            new CadenceScore(RecurrenceCadence.Monthly, 0.9, 3),
            new CadenceScore(RecurrenceCadence.Quarterly, 0.9, 1),
        ])!.Cadence.ShouldBe(RecurrenceCadence.Quarterly);
        RecurringDetector.PickBest(
        [
            new CadenceScore(RecurrenceCadence.Biweekly, 1.0, 5),
            new CadenceScore(RecurrenceCadence.Semimonthly, 0.75, 1),
        ])!.Cadence.ShouldBe(RecurrenceCadence.Semimonthly);
        RecurringDetector.PickBest(
        [
            new CadenceScore(RecurrenceCadence.Biweekly, 1.0, 5),
            new CadenceScore(RecurrenceCadence.Semimonthly, 0.6, 1),
        ])!.Cadence.ShouldBe(RecurrenceCadence.Biweekly);
    }

    // Labeled accuracy --------------------------------------------------------------------------

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Accuracy_on_a_labeled_generated_ledger(int seed)
    {
        var (transactions, labels) = RecurringFixtureGenerator.Generate(400, AsOf, seed);
        var detected = RecurringDetector.Detect(transactions, AsOf).ToDictionary(d => d.Key);

        var correct = labels.Count(l => detected.TryGetValue(l.Key, out var d) ? d.Cadence == l.Expected : l.Expected is null);
        var falsePositives = labels.Count(l => l.Expected is null && detected.ContainsKey(l.Key));

        output.WriteLine($"seed {seed}: {correct} of {labels.Count} groups classified correctly, {falsePositives} false positives");
        ((double)correct / labels.Count).ShouldBeGreaterThanOrEqualTo(0.97);
        falsePositives.ShouldBe(0);
    }
}
