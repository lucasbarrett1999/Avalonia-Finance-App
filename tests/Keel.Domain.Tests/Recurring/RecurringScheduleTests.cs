using System.Globalization;
using Keel.Domain.Recurring;

namespace Keel.Domain.Tests.Recurring;

public class RecurringScheduleTests
{
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static List<DateOnly> Ds(params string[] iso) => iso.Select(D).ToList();

    [Theory]
    [InlineData("2026-01-31", -1)]
    [InlineData("2026-02-28", -1)]
    [InlineData("2028-02-28", 28)]
    [InlineData("2028-02-29", -1)]
    [InlineData("2026-04-30", -1)]
    [InlineData("2026-05-30", 30)]
    [InlineData("2026-05-01", 1)]
    public void Day_key_marks_the_last_day_of_the_month(string date, int key) =>
        RecurringSchedule.DayKey(D(date)).ShouldBe(key);

    [Theory]
    [InlineData(2026, 2, 31, "2026-02-28")]
    [InlineData(2028, 2, 30, "2028-02-29")]
    [InlineData(2026, 2, -1, "2026-02-28")]
    [InlineData(2026, 4, 31, "2026-04-30")]
    [InlineData(2026, 4, 15, "2026-04-15")]
    public void OnDay_clamps_to_the_month(int year, int month, int key, string expected) =>
        RecurringSchedule.OnDay(year, month, key).ShouldBe(D(expected));

    [Theory]
    [InlineData(new[] { "2026-01-31", "2026-02-28", "2026-03-31", "2026-04-30" }, -1)]
    [InlineData(new[] { "2025-11-30", "2025-12-30", "2026-01-30", "2026-02-28" }, 30)]
    [InlineData(new[] { "2026-06-01", "2026-07-01", "2026-07-31" }, 1)]
    [InlineData(new[] { "2026-06-15", "2026-07-16", "2026-08-16", "2026-09-15" }, 15)]
    [InlineData(new[] { "2027-01-29", "2027-02-28", "2027-03-29" }, 29)]
    public void Month_day_anchor(string[] dates, int anchor) =>
        RecurringSchedule.MonthDayAnchor(Ds(dates)).ShouldBe(anchor);

    [Theory]
    [InlineData(new[] { "2026-06-01", "2026-06-15", "2026-07-01", "2026-07-15" }, 1, 15)]
    [InlineData(new[] { "2026-06-15", "2026-06-30", "2026-07-15", "2026-07-31" }, 15, -1)]
    [InlineData(new[] { "2026-06-15", "2026-07-15", "2026-08-15" }, 15, 30)]   // no second day seen: 15 days away
    [InlineData(new[] { "2026-06-01", "2026-06-02", "2026-07-01" }, 1, 16)]
    public void Semimonthly_anchors(string[] dates, int first, int second) =>
        RecurringSchedule.SemimonthlyAnchors(Ds(dates)).ShouldBe((first, second));

    [Fact]
    public void Mode_ties_go_to_the_most_recent_value()
    {
        RecurringSchedule.Mode([1, 2, 2, 1]).ShouldBe(1);
        RecurringSchedule.Mode([1, 2, 2, 3]).ShouldBe(2);
        Should.Throw<ArgumentException>(() => RecurringSchedule.Mode(Array.Empty<int>()));
    }

    [Theory]
    [InlineData(RecurrenceCadence.Weekly, "2026-09-25", "2026-09-18", "FREQ=WEEKLY;BYDAY=FR")]
    [InlineData(RecurrenceCadence.Biweekly, "2026-10-02", "2026-09-18", "FREQ=WEEKLY;INTERVAL=2;BYDAY=FR")]
    [InlineData(RecurrenceCadence.Semimonthly, "2026-10-01", "2026-09-15", "FREQ=MONTHLY;BYMONTHDAY=1,15")]
    [InlineData(RecurrenceCadence.Semimonthly, "2026-09-30", "2026-09-15", "FREQ=MONTHLY;BYMONTHDAY=15,-1")]
    [InlineData(RecurrenceCadence.Semimonthly, "2026-09-15", "2026-08-31", "FREQ=MONTHLY;BYMONTHDAY=15,-1")]
    [InlineData(RecurrenceCadence.Semimonthly, "2026-09-15", "2026-09-14", "FREQ=MONTHLY;BYMONTHDAY=15,30")]
    [InlineData(RecurrenceCadence.Monthly, "2026-10-12", "2026-09-12", "FREQ=MONTHLY;BYMONTHDAY=12")]
    [InlineData(RecurrenceCadence.Monthly, "2026-02-28", "2026-01-31", "FREQ=MONTHLY;BYMONTHDAY=-1")]
    [InlineData(RecurrenceCadence.Monthly, "2026-02-28", "2026-01-30", "FREQ=MONTHLY;BYMONTHDAY=30")]
    [InlineData(RecurrenceCadence.Monthly, "2026-04-30", "2026-03-30", "FREQ=MONTHLY;BYMONTHDAY=30")]
    [InlineData(RecurrenceCadence.Monthly, "2026-06-30", "2026-04-30", "FREQ=MONTHLY;BYMONTHDAY=30")]
    [InlineData(RecurrenceCadence.Quarterly, "2026-10-15", "2026-07-15", "FREQ=MONTHLY;INTERVAL=3;BYMONTHDAY=15")]
    [InlineData(RecurrenceCadence.Yearly, "2027-08-04", "2026-08-04", "FREQ=YEARLY;BYMONTH=8;BYMONTHDAY=4")]
    [InlineData(RecurrenceCadence.Yearly, "2027-02-28", "2026-02-28", "FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=28")]
    [InlineData(RecurrenceCadence.Yearly, "2025-02-28", "2024-02-29", "FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=-1")]
    [InlineData(RecurrenceCadence.Yearly, "2027-03-01", "2026-02-28", "FREQ=YEARLY;BYMONTH=3;BYMONTHDAY=1")]
    public void Infers_the_projection_rule_of_a_stored_item(RecurrenceCadence cadence, string next, string lastSeen, string rule) =>
        RecurringSchedule.InferRule(cadence, D(next), D(lastSeen)).ToString().ShouldBe(rule);

    [Theory]
    [InlineData(RecurrenceCadence.Semimonthly, new[] { "2026-08-01", "2026-08-15", "2026-09-01", "2026-09-14" }, "2026-10-01")]
    [InlineData(RecurrenceCadence.Semimonthly, new[] { "2026-08-15", "2026-08-31", "2026-09-15" }, "2026-09-30")]
    [InlineData(RecurrenceCadence.Quarterly, new[] { "2026-01-15", "2026-04-15", "2026-07-17" }, "2026-10-15")]
    [InlineData(RecurrenceCadence.Biweekly, new[] { "2026-08-21", "2026-09-04", "2026-09-17" }, "2026-10-02")]
    public void Next_expected_date(RecurrenceCadence cadence, string[] dates, string next) =>
        RecurringSchedule.NextExpected(cadence, Ds(dates)).ShouldBe(D(next));
}
