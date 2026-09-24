using Keel.Domain.Reports;

namespace Keel.Domain.Tests.Reports;

public sealed class ReportMathTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Theory]
    [InlineData("2026-08-01", "2026-08-31", "2026-07-01", "2026-07-31")]
    [InlineData("2026-01-01", "2026-03-31", "2025-10-01", "2025-12-31")]
    [InlineData("2025-10-01", "2026-09-30", "2024-10-01", "2025-09-30")]
    [InlineData("2026-03-01", "2026-03-31", "2026-02-01", "2026-02-28")]
    [InlineData("2026-08-05", "2026-08-14", "2026-07-26", "2026-08-04")]
    [InlineData("2026-03-15", "2026-03-15", "2026-03-14", "2026-03-14")]
    public void Previous_period(string from, string to, string previousFrom, string previousTo)
    {
        var (f, t) = ReportPeriod.Previous(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture));
        f.ShouldBe(DateOnly.Parse(previousFrom, System.Globalization.CultureInfo.InvariantCulture));
        t.ShouldBe(DateOnly.Parse(previousTo, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Previous_period_rejects_reversed_ranges() =>
        Should.Throw<ArgumentException>(() => ReportPeriod.Previous(D(2026, 8, 2), D(2026, 8, 1)));

    [Fact]
    public void Months_and_month_end_points()
    {
        ReportPeriod.Months(D(2026, 1, 15), D(2026, 3, 1)).ShouldBe([D(2026, 1, 1), D(2026, 2, 1), D(2026, 3, 1)]);
        ReportPeriod.MonthEnd(D(2024, 2, 10)).ShouldBe(D(2024, 2, 29));
        ReportPeriod.MonthEndPoints(D(2026, 1, 1), D(2026, 12, 31), today: D(2026, 3, 10))
            .ShouldBe([D(2026, 1, 31), D(2026, 2, 28), D(2026, 3, 10)]);
        ReportPeriod.MonthEndPoints(D(2026, 1, 1), D(2026, 2, 15), today: D(2026, 9, 1))
            .ShouldBe([D(2026, 1, 31), D(2026, 2, 15)]);
        ReportPeriod.MonthEndPoints(D(2026, 5, 1), D(2026, 6, 30), today: D(2026, 4, 1)).ShouldBeEmpty();
        ReportPeriod.IsWholeMonths(D(2026, 2, 1), D(2026, 2, 28)).ShouldBeTrue();
        ReportPeriod.IsWholeMonths(D(2026, 2, 1), D(2026, 2, 27)).ShouldBeFalse();
    }

    [Fact]
    public void Balance_series_is_the_ledger_sum_without_snapshots()
    {
        var points = new[] { D(2026, 1, 31), D(2026, 2, 28), D(2026, 3, 31) };
        var changes = new[] { new LedgerChange(D(2026, 2, 1), -30), new LedgerChange(D(2026, 1, 1), 100), new LedgerChange(D(2026, 4, 1), 7) };
        BalanceSeries.At(points, changes, []).ShouldBe([100, 70, 70]);
    }

    [Fact]
    public void Balance_series_uses_the_latest_snapshot_on_or_before_each_point_plus_later_activity()
    {
        var points = new[] { D(2026, 1, 31), D(2026, 2, 28), D(2026, 3, 31), D(2026, 4, 30) };
        var changes = new[]
        {
            new LedgerChange(D(2026, 1, 5), 1_000),
            new LedgerChange(D(2026, 2, 10), 200),  // before the snapshot: already in it
            new LedgerChange(D(2026, 2, 15), 50),   // on the snapshot date: already in it
            new LedgerChange(D(2026, 2, 20), 300),  // after the snapshot: added
            new LedgerChange(D(2026, 4, 2), 10),
        };
        var snapshots = new[] { new ReportedBalance(D(2026, 3, 31), 2_000), new ReportedBalance(D(2026, 2, 15), 1_500) };
        BalanceSeries.At(points, changes, snapshots).ShouldBe([1_000, 1_800, 2_000, 2_010]);
    }

    [Fact]
    public void Goal_projection()
    {
        var month = D(2026, 9, 1);
        GoalProjection.Project(available: 400, target: 1_000, monthlyContribution: 200, month)
            .ShouldBe(new GoalProjectionResult(false, 600, 3, D(2026, 12, 1)));
        GoalProjection.Project(400, 1_000, 250, D(2026, 9, 17)).CompletionMonth.ShouldBe(D(2026, 12, 1)); // 600 / 250 rounds up to 3
        GoalProjection.Project(1_000, 1_000, 0, month).ShouldBe(new GoalProjectionResult(true, 0, 0, month));
        var stalled = GoalProjection.Project(10, 1_000, 0, month);
        stalled.MonthsToGo.ShouldBeNull();
        stalled.CompletionMonth.ShouldBeNull();
        stalled.IsOnTrackFor(D(2030, 1, 1)).ShouldBeFalse();

        var result = GoalProjection.Project(400, 1_000, 200, month);
        result.IsOnTrackFor(D(2026, 12, 15)).ShouldBeTrue();
        result.IsOnTrackFor(D(2026, 11, 30)).ShouldBeFalse();
        GoalProjection.Project(0, long.MaxValue / 2, 1, month).MonthsToGo.ShouldBe(1200);
    }

    [Fact]
    public void Average_pace_rounds_half_to_even()
    {
        GoalProjection.AveragePace([100, 0, 50]).ShouldBe(50);
        GoalProjection.AveragePace([1, 0, 0, 0]).ShouldBe(0);   // 0.25
        GoalProjection.AveragePace([5, 0]).ShouldBe(2);          // 2.5 -> 2
        GoalProjection.AveragePace([7, 0]).ShouldBe(4);          // 3.5 -> 4
        GoalProjection.AveragePace([]).ShouldBe(0);
        GoalProjection.AveragePace([-30, 0, 0]).ShouldBe(-10);
    }
}
