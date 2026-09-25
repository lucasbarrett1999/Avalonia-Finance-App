using Keel.Domain.Reports;

namespace Keel.Domain.Tests.Reports;

public sealed class AgeOfMoneyTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static DailyCashFlow Day(DateOnly date, long inflow = 0, long outflow = 0, int count = -1) =>
        new(date, inflow, outflow, count >= 0 ? count : (outflow > 0 ? 1 : 0));

    [Fact]
    public void Age_is_the_days_between_the_funding_inflow_and_the_outflow()
    {
        var days = new[] { Day(D(1, 1), inflow: 3_000_00), Day(D(1, 11), outflow: 100_00) };
        var point = AgeOfMoney.At(days, [D(1, 31)]).Single();
        point.Days.ShouldBe(10);
        point.OutflowCount.ShouldBe(1);
        point.Window.ShouldBe([new AgeOfMoneyOutflow(D(1, 11), 1, 10m)]);
    }

    [Fact]
    public void Outflows_spend_the_oldest_money_first_weighted_by_amount()
    {
        // 100 from Jan 1 (31 days) and 50 from Jan 15 (17 days): (3100 + 850) / 150 = 26.33 -> 26.
        var days = new[] { Day(D(1, 1), inflow: 100_00), Day(D(1, 15), inflow: 100_00), Day(D(2, 1), outflow: 150_00) };
        AgeOfMoney.At(days, [D(2, 1)]).Single().Days.ShouldBe(26);

        // The next outflow uses what is left of the Jan 15 money: 20 days.
        var more = days.Append(Day(D(2, 4), outflow: 50_00));
        var point = AgeOfMoney.At(more, [D(2, 28)]).Single();
        point.Window.Select(w => w.AgeDays).ShouldBe([20m, 3950m / 150]);
        point.Days.ShouldBe(23); // (20 + 26.33) / 2 = 23.17
    }

    [Fact]
    public void Averages_only_the_last_ten_outflows()
    {
        // One inflow on Jan 1, then one $1 outflow every day from Jan 2 to Jan 13 (ages 1..12).
        var days = new List<DailyCashFlow> { Day(D(1, 1), inflow: 1_000_00) };
        days.AddRange(Enumerable.Range(2, 12).Select(d => Day(D(1, d), outflow: 1_00)));
        var point = AgeOfMoney.At(days, [D(1, 31)]).Single();
        point.OutflowCount.ShouldBe(10);
        point.Window.Select(w => w.Date).ShouldBe(Enumerable.Range(4, 10).Reverse().Select(d => D(1, d)));
        point.Days.ShouldBe(8); // mean of 3..12 = 7.5 -> 8 (half to even)
    }

    [Fact]
    public void A_busy_day_at_the_edge_of_the_window_counts_only_its_latest_outflows()
    {
        // Jan 5: 8 outflows (age 4); Jan 10: 4 outflows (age 9). Window: 4 x 9 + 6 x 4 = 60 / 10 = 6.
        var days = new[] { Day(D(1, 1), inflow: 1_000_00), Day(D(1, 5), outflow: 80_00, count: 8), Day(D(1, 10), outflow: 40_00, count: 4) };
        var point = AgeOfMoney.At(days, [D(1, 10)]).Single();
        point.Window.ShouldBe([new AgeOfMoneyOutflow(D(1, 10), 4, 9m), new AgeOfMoneyOutflow(D(1, 5), 6, 4m)]);
        point.Days.ShouldBe(6);
    }

    [Fact]
    public void Same_day_inflow_funds_same_day_outflow_at_age_zero_when_nothing_older_is_left()
    {
        var days = new[] { Day(D(3, 1), inflow: 50_00), Day(D(3, 2), inflow: 100_00, outflow: 80_00) };
        // 50 from Mar 1 (1 day) + 30 from Mar 2 (0 days) = 50 / 80 = 0.625 -> 1.
        AgeOfMoney.At(days, [D(3, 2)]).Single().Days.ShouldBe(1);
    }

    [Fact]
    public void Unfunded_outflows_are_left_out_and_repaid_by_the_next_inflows()
    {
        // Overdrawn by 40 on Jan 2 (no inflow yet): no age. The Jan 5 inflow of 100 first repays the 40,
        // so the Jan 20 outflow of 60 is funded by the remaining 60 from Jan 5: 15 days.
        var days = new[] { Day(D(1, 2), outflow: 40_00), Day(D(1, 5), inflow: 100_00), Day(D(1, 20), outflow: 60_00) };
        var points = AgeOfMoney.At(days, [D(1, 3), D(1, 31)]);
        points[0].Days.ShouldBeNull();
        points[0].Window.ShouldBeEmpty();
        points[1].Days.ShouldBe(15);
        points[1].OutflowCount.ShouldBe(1);

        // A partly funded day counts with the age of its funded part.
        var partly = new[] { Day(D(1, 1), inflow: 10_00), Day(D(1, 11), outflow: 30_00) };
        AgeOfMoney.At(partly, [D(1, 11)]).Single().Days.ShouldBe(10);
    }

    [Fact]
    public void Points_are_evaluated_in_any_order_and_before_activity_have_no_age()
    {
        var days = new[] { Day(D(1, 1), inflow: 500_00), Day(D(2, 10), outflow: 10_00), Day(D(3, 20), outflow: 10_00) };
        var points = AgeOfMoney.At(days, [D(3, 31), D(1, 15), D(2, 28)]);
        points.Select(p => p.Date).ShouldBe([D(3, 31), D(1, 15), D(2, 28)]);
        points[1].Days.ShouldBeNull();
        points[2].Days.ShouldBe(40);
        points[0].Days.ShouldBe(59); // (40 + 78) / 2
        AgeOfMoney.At([], [D(1, 1)]).Single().Days.ShouldBeNull();
    }

    [Fact]
    public void Rejects_negative_input() =>
        Should.Throw<ArgumentException>(() => AgeOfMoney.At([new DailyCashFlow(D(1, 1), -1, 0, 0)], [D(1, 2)]));
}
