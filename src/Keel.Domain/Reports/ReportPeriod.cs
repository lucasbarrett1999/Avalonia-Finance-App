using Keel.Domain.Budgeting;

namespace Keel.Domain.Reports;

/// <summary>Date-range helpers for reports (F-REP-1..3). Ranges are inclusive on both ends.</summary>
public static class ReportPeriod
{
    /// <summary>Whether the range covers whole calendar months (first day to last day).</summary>
    public static bool IsWholeMonths(DateOnly from, DateOnly to) =>
        from.Day == 1 && to.AddDays(1).Day == 1 && to >= from;

    /// <summary>
    /// The period compared against (F-REP-1 "compare to previous period"): the same number of
    /// whole months immediately before a month-aligned range, otherwise the same number of days
    /// immediately before.
    /// </summary>
    public static (DateOnly From, DateOnly To) Previous(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new ArgumentException("The range ends before it starts.", nameof(to));
        }

        if (IsWholeMonths(from, to))
        {
            var months = BudgetMonth.Between(from, to) + 1;
            return (from.AddMonths(-months), from.AddDays(-1));
        }

        var days = to.DayNumber - from.DayNumber + 1;
        return (from.AddDays(-days), from.AddDays(-1));
    }

    /// <summary>The first day of every month that intersects the range, in order.</summary>
    public static IReadOnlyList<DateOnly> Months(DateOnly from, DateOnly to)
    {
        var result = new List<DateOnly>();
        for (var month = BudgetMonth.Of(from); month <= to; month = month.AddMonths(1))
        {
            result.Add(month);
        }

        return result;
    }

    /// <summary>The last day of the month containing <paramref name="date"/>.</summary>
    public static DateOnly MonthEnd(DateOnly date) => BudgetMonth.NextOf(date).AddDays(-1);

    /// <summary>
    /// Net-worth points (F-REP-3): the end of every month intersecting the range, the last one
    /// clamped to <paramref name="to"/>, and none after <paramref name="today"/> (the last point is
    /// then today).
    /// </summary>
    public static IReadOnlyList<DateOnly> MonthEndPoints(DateOnly from, DateOnly to, DateOnly today)
    {
        var last = to < today ? to : today;
        var result = new List<DateOnly>();
        if (last < from)
        {
            return result;
        }

        foreach (var month in Months(from, last))
        {
            var end = MonthEnd(month);
            result.Add(end < last ? end : last);
        }

        return result;
    }
}
