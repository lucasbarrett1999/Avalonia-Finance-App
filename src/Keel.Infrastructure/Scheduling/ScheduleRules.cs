using Keel.Domain.Scheduling;

namespace Keel.Infrastructure.Scheduling;

/// <summary>A rule as stored: explicit day parts, no <c>COUNT</c>/<c>UNTIL</c> (they become the end date).</summary>
/// <param name="Rule">The explicit rule without <c>COUNT</c> or <c>UNTIL</c>.</param>
/// <param name="First">First occurrence on or after the start date, or null when there is none.</param>
/// <param name="EndDate">Last possible instance (the earlier of the end date, <c>UNTIL</c> and the <c>COUNT</c>-th occurrence).</param>
internal sealed record StoredSchedule(RecurrenceRule Rule, DateOnly? First, DateOnly? EndDate);

/// <summary>
/// Normalizes scheduled-transaction rules so the next date alone can anchor them (ADR 0030 left this
/// to the scheduling service; ADR 0035): the weekday, day of month and month that a rule would take
/// from its start date are written into the rule, and <c>COUNT</c> and <c>UNTIL</c> become the end
/// date. Every stored next date is an occurrence, so evaluating from it keeps the <c>INTERVAL</c>
/// phase (the fortnight, the quarter) of the original start.
/// </summary>
internal static class ScheduleRules
{
    /// <summary>Parses <paramref name="text"/> and normalizes it for a schedule starting on <paramref name="start"/>.</summary>
    /// <exception cref="RecurrenceRuleFormatException">The rule is invalid.</exception>
    public static StoredSchedule Normalize(string text, DateOnly start, DateOnly? endDate)
    {
        var parsed = RecurrenceRule.Parse(text);
        var end = endDate;
        if (parsed.Until is { } until)
        {
            end = Min(end, until);
        }

        if (parsed.Count is { } count)
        {
            var last = parsed.Occurrences(start, start, DateOnly.MaxValue).Take(count).Select(d => (DateOnly?)d).LastOrDefault();
            end = last is null ? start.AddDays(-1) : Min(end, last.Value);
        }

        var rule = Explicit(parsed, start);
        var first = rule.First(start);
        return new StoredSchedule(rule, first is { } f && end is { } e && f > e ? null : first, end);
    }

    /// <summary>The rule with the parts it would take from the start date made explicit, and no COUNT/UNTIL.</summary>
    public static RecurrenceRule Explicit(RecurrenceRule rule, DateOnly start)
    {
        ArgumentNullException.ThrowIfNull(rule);
        IEnumerable<WeekdayOccurrence> byDay = rule.ByDay;
        IEnumerable<int> byMonthDay = rule.ByMonthDay;
        IEnumerable<int> byMonth = rule.ByMonth;
        switch (rule.Frequency)
        {
            case RecurrenceFrequency.Weekly when rule.ByDay.Count == 0:
                byDay = [new WeekdayOccurrence(start.DayOfWeek)];
                break;
            case RecurrenceFrequency.Monthly when rule.ByDay.Count == 0 && rule.ByMonthDay.Count == 0:
                byMonthDay = [start.Day];
                break;
            case RecurrenceFrequency.Yearly:
                if (rule.ByMonth.Count == 0)
                {
                    byMonth = [start.Month];
                }

                if (rule.ByDay.Count == 0 && rule.ByMonthDay.Count == 0)
                {
                    byMonthDay = [start.Day];
                }

                break;
        }

        return RecurrenceRule.Create(rule.Frequency, rule.Interval, byDay, byMonthDay, byMonth, null, null, rule.WeekStart);
    }

    /// <summary>The next occurrence after <paramref name="date"/> of a stored schedule anchored at <paramref name="anchor"/>.</summary>
    public static DateOnly? After(RecurrenceRule rule, DateOnly anchor, DateOnly date) => rule.NextAfter(anchor, date);

    private static DateOnly Min(DateOnly? a, DateOnly b) => a is { } x && x < b ? x : b;
}
