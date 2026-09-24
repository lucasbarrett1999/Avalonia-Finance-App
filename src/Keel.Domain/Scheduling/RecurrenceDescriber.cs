using System.Globalization;

namespace Keel.Domain.Scheduling;

/// <summary>English descriptions of recurrence rules ("Every 2 weeks on Friday").</summary>
internal static class RecurrenceDescriber
{
    private static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    public static string Describe(RecurrenceRule rule, DateOnly? start)
    {
        var text = rule.Frequency switch
        {
            RecurrenceFrequency.Daily => DescribeDaily(rule),
            RecurrenceFrequency.Weekly => DescribeWeekly(rule, start),
            RecurrenceFrequency.Monthly => DescribeMonthly(rule, start),
            RecurrenceFrequency.Yearly => DescribeYearly(rule, start),
            _ => rule.ToString(),
        };

        if (rule.Count is { } count)
        {
            text += count == 1 ? ", once" : string.Create(CultureInfo.InvariantCulture, $", {count} times");
        }

        if (rule.Until is { } until)
        {
            text += ", until " + until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return text;
    }

    /// <summary>1st, 2nd, 3rd, 4th, 11th, 12th, 13th, 21st, 22nd, 23rd, 31st.</summary>
    public static string Ordinal(int n)
    {
        var suffix = (n % 100) is 11 or 12 or 13
            ? "th"
            : (n % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th",
            };
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    public static string DayName(DayOfWeek day) => day.ToString();

    public static string MonthName(int month) => MonthNames[month - 1];

    private static string Every(int interval, string singular, string plural) =>
        interval == 1 ? "Every " + singular : string.Create(CultureInfo.InvariantCulture, $"Every {interval} {plural}");

    private static string DescribeDaily(RecurrenceRule rule)
    {
        if (rule.ByDay.Count == 0)
        {
            return Every(rule.Interval, "day", "days");
        }

        var weekdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        if (rule.Interval == 1 && rule.ByDay.Count == 5 && weekdays.All(d => rule.ByDay.Any(b => b.Day == d)))
        {
            return "Every weekday";
        }

        return Every(rule.Interval, "day", "days") + " on " + JoinAnd(rule.ByDay.Select(d => DayName(d.Day)).ToList());
    }

    private static string DescribeWeekly(RecurrenceRule rule, DateOnly? start)
    {
        var head = Every(rule.Interval, "week", "weeks");
        var days = rule.ByDay.Count > 0
            ? rule.ByDay.Select(d => d.Day).ToList()
            : start is { } s ? [s.DayOfWeek] : [];
        return days.Count == 0 ? head : head + " on " + JoinAnd(days.Select(DayName).ToList());
    }

    private static string DescribeMonthly(RecurrenceRule rule, DateOnly? start)
    {
        var head = Every(rule.Interval, "month", "months");
        var on = DayPhrase(rule, start);
        return on.Length == 0 ? head : head + " on " + on;
    }

    private static string DescribeYearly(RecurrenceRule rule, DateOnly? start)
    {
        var head = Every(rule.Interval, "year", "years");
        var months = rule.ByMonth.Count > 0
            ? rule.ByMonth.ToList()
            : start is { } s ? [s.Month] : [];
        if (months.Count == 0)
        {
            return head;
        }

        var monthText = JoinAnd(months.Select(MonthName).ToList());
        if (rule.ByDay.Count == 0)
        {
            var days = rule.ByMonthDay.Count > 0
                ? rule.ByMonthDay.ToList()
                : start is { } st ? [st.Day] : [];
            if (days.Count == 1 && days[0] > 0 && months.Count == 1)
            {
                return head + " on " + monthText + " " + days[0].ToString(CultureInfo.InvariantCulture);
            }

            if (days.Count == 0)
            {
                return head + " in " + monthText;
            }

            return head + " on the " + JoinAnd(days.Select(MonthDayText).ToList()) + " of " + monthText;
        }

        return head + " on " + JoinAnd(rule.ByDay.Select(WeekdayText).ToList()) + " of " + monthText;
    }

    private static string DayPhrase(RecurrenceRule rule, DateOnly? start)
    {
        if (rule.ByDay.Count > 0)
        {
            return JoinAnd(rule.ByDay.Select(WeekdayText).ToList());
        }

        var days = rule.ByMonthDay.Count > 0
            ? rule.ByMonthDay.ToList()
            : start is { } s ? [s.Day] : [];
        return days.Count == 0 ? string.Empty : "the " + JoinAnd(days.Select(MonthDayText).ToList());
    }

    private static string MonthDayText(int day) => day switch
    {
        > 0 => Ordinal(day),
        -1 => "last day",
        _ => Ordinal(-day) + "-to-last day",
    };

    private static string WeekdayText(WeekdayOccurrence occurrence) => occurrence.Ordinal switch
    {
        0 => "every " + DayName(occurrence.Day),
        -1 => "the last " + DayName(occurrence.Day),
        < 0 => "the " + Ordinal(-occurrence.Ordinal) + "-to-last " + DayName(occurrence.Day),
        _ => "the " + Ordinal(occurrence.Ordinal) + " " + DayName(occurrence.Day),
    };
}
