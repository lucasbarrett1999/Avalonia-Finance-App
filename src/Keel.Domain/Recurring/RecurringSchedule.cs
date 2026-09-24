using Keel.Domain.Scheduling;

namespace Keel.Domain.Recurring;

/// <summary>
/// Calendar anchoring for recurring items: which weekday or day(s) of month an item keeps, the next
/// expected date (PRD 6.6 step 6), and the <see cref="RecurrenceRule"/> that projects an item forward.
/// Interpretations are in <c>docs/decisions/0031-recurring-detection-interpretations.md</c>.
/// </summary>
public static class RecurringSchedule
{
    /// <summary>Day-of-month key for "last day of the month" (as in <c>BYMONTHDAY=-1</c>).</summary>
    public const int LastDay = -1;

    /// <summary>Minimum distance in days between the two days of a twice-monthly pattern.</summary>
    public const int MinSemimonthlySpread = 8;

    /// <summary>True when <paramref name="date"/> is the last day of its month.</summary>
    public static bool IsLastDayOfMonth(DateOnly date) => date.Day == DateTime.DaysInMonth(date.Year, date.Month);

    /// <summary>The day-of-month key of a date: <see cref="LastDay"/> for a month's last day, else the day.</summary>
    public static int DayKey(DateOnly date) => IsLastDayOfMonth(date) ? LastDay : date.Day;

    /// <summary>The date for a day-of-month key in a month, clamped to the month's length.</summary>
    public static DateOnly OnDay(int year, int month, int dayKey)
    {
        var dim = DateTime.DaysInMonth(year, month);
        return new DateOnly(year, month, dayKey == LastDay ? dim : Math.Min(dayKey, dim));
    }

    /// <summary>
    /// The most common value, ties going to the value of the most recent item (the list is oldest first).
    /// </summary>
    public static T Mode<T>(IReadOnlyList<T> values)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }

        var counts = new Dictionary<T, int>();
        foreach (var v in values)
        {
            counts[v] = counts.GetValueOrDefault(v) + 1;
        }

        var max = counts.Values.Max();
        for (var i = values.Count - 1; i >= 0; i--)
        {
            if (counts[values[i]] == max)
            {
                return values[i];
            }
        }

        return values[^1];
    }

    /// <summary>
    /// The day-of-month anchor of a monthly, quarterly or yearly item: the day key (a day, or
    /// <see cref="LastDay"/>) that the most recent dates (oldest first) fall on exactly, allowing for
    /// clamping in short months, so the 30th stays the 30th after February 28. Ties go to the key of
    /// the most recent date; 31 is reported as <see cref="LastDay"/>.
    /// </summary>
    public static int MonthDayAnchor(IReadOnlyList<DateOnly> recentDates) => BestAnchor(recentDates, _ => true);

    /// <summary>
    /// The two day-of-month anchors of a twice-monthly item: the best anchor as in
    /// <see cref="MonthDayAnchor"/>, then the best anchor at least <see cref="MinSemimonthlySpread"/>
    /// days away from it (falling back to 15 days away). Returned in <c>BYMONTHDAY</c> order.
    /// </summary>
    public static (int First, int Second) SemimonthlyAnchors(IReadOnlyList<DateOnly> recentDates)
    {
        var a = BestAnchor(recentDates, _ => true);
        var b = recentDates.Any(d => SpreadDays(DayKey(d), a) >= MinSemimonthlySpread)
            ? BestAnchor(recentDates, k => SpreadDays(k, a) >= MinSemimonthlySpread)
            : Opposite(a);
        return Order(a, b);
    }

    /// <summary>
    /// Next expected date of a detected pattern (PRD 6.6 step 6). Weekly and biweekly: last date plus
    /// the period, moved to the usual weekday. Monthly, quarterly and yearly: the anchor day nearest to
    /// the last date plus one period (so an early or late payment does not shift the schedule).
    /// Twice monthly: the anchor day nearest to the last date plus 15 days.
    /// </summary>
    public static DateOnly NextExpected(RecurrenceCadence cadence, IReadOnlyList<DateOnly> recentDates)
    {
        ArgumentNullException.ThrowIfNull(recentDates);
        var last = recentDates[^1];
        switch (cadence)
        {
            case RecurrenceCadence.Weekly:
            case RecurrenceCadence.Biweekly:
                {
                    var ideal = last.AddDays(cadence == RecurrenceCadence.Weekly ? 7 : 14);
                    var weekday = Mode(recentDates.Select(d => d.DayOfWeek).ToList());
                    var shift = (((int)weekday - (int)ideal.DayOfWeek) + 7) % 7;
                    return ideal.AddDays(shift > 3 ? shift - 7 : shift);
                }

            case RecurrenceCadence.Semimonthly:
                {
                    var (a, b) = SemimonthlyAnchors(recentDates);
                    return Nearest(last.AddDays(15), last, [a, b]);
                }

            case RecurrenceCadence.Monthly:
            case RecurrenceCadence.Quarterly:
            case RecurrenceCadence.Yearly:
                {
                    var ideal = cadence switch
                    {
                        RecurrenceCadence.Monthly => last.AddMonths(1),
                        RecurrenceCadence.Quarterly => last.AddMonths(3),
                        _ => last.AddYears(1),
                    };
                    return Nearest(ideal, last, [MonthDayAnchor(recentDates)]);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(cadence), cadence, "Unknown cadence.");
        }
    }

    /// <summary>The rule for a detected pattern, anchored as <see cref="NextExpected"/> anchors it.</summary>
    public static RecurrenceRule RuleFor(RecurrenceCadence cadence, IReadOnlyList<DateOnly> recentDates, DateOnly nextExpected)
    {
        ArgumentNullException.ThrowIfNull(recentDates);
        return cadence switch
        {
            RecurrenceCadence.Weekly => RecurrenceRule.Weekly(nextExpected.DayOfWeek),
            RecurrenceCadence.Biweekly => RecurrenceRule.Weekly(nextExpected.DayOfWeek, 2),
            RecurrenceCadence.Semimonthly => TwiceMonthly(SemimonthlyAnchors(recentDates)),
            RecurrenceCadence.Monthly => RecurrenceRule.MonthlyOnDay(MonthDayAnchor(recentDates)),
            RecurrenceCadence.Quarterly => RecurrenceRule.MonthlyOnDay(MonthDayAnchor(recentDates), 3),
            RecurrenceCadence.Yearly => RecurrenceRule.Yearly(nextExpected.Month, MonthDayAnchor(recentDates)),
            _ => throw new ArgumentOutOfRangeException(nameof(cadence), cadence, "Unknown cadence."),
        };
    }

    /// <summary>
    /// Reconstructs the projection rule of a stored <c>RecurringItem</c>, which keeps only its cadence,
    /// next expected date and last seen date. Apply the rule from <paramref name="nextExpected"/>.
    /// A next date on a month's last day is read together with the last seen date, so an item on the
    /// 30th or 31st is not pulled to the 28th after February (ADR 0031).
    /// </summary>
    public static RecurrenceRule InferRule(RecurrenceCadence cadence, DateOnly nextExpected, DateOnly lastSeen) => cadence switch
    {
        RecurrenceCadence.Weekly => RecurrenceRule.Weekly(nextExpected.DayOfWeek),
        RecurrenceCadence.Biweekly => RecurrenceRule.Weekly(nextExpected.DayOfWeek, 2),
        RecurrenceCadence.Semimonthly => TwiceMonthly(InferSemimonthly(nextExpected, lastSeen)),
        RecurrenceCadence.Monthly => RecurrenceRule.MonthlyOnDay(InferMonthDay(nextExpected, lastSeen)),
        RecurrenceCadence.Quarterly => RecurrenceRule.MonthlyOnDay(InferMonthDay(nextExpected, lastSeen), 3),
        RecurrenceCadence.Yearly => RecurrenceRule.Yearly(
            nextExpected.Month,
            lastSeen.Month == nextExpected.Month ? InferMonthDay(nextExpected, lastSeen) : nextExpected.Day),
        _ => throw new ArgumentOutOfRangeException(nameof(cadence), cadence, "Unknown cadence."),
    };

    private static int InferMonthDay(DateOnly next, DateOnly lastSeen)
    {
        if (!IsLastDayOfMonth(next))
        {
            return next.Day;
        }

        if (IsLastDayOfMonth(lastSeen))
        {
            return lastSeen.Day == next.Day ? next.Day : LastDay;
        }

        return Math.Max(next.Day, lastSeen.Day);
    }

    private static (int, int) InferSemimonthly(DateOnly next, DateOnly lastSeen)
    {
        var a = DayKey(next);
        var b = DayKey(lastSeen);
        return Order(a, SpreadDays(a, b) >= MinSemimonthlySpread ? b : Opposite(a));
    }

    private static RecurrenceRule TwiceMonthly((int First, int Second) days) =>
        days.First == days.Second ? RecurrenceRule.MonthlyOnDay(days.First) : RecurrenceRule.TwiceMonthly(days.First, days.Second);

    private static int BestAnchor(IReadOnlyList<DateOnly> dates, Func<int, bool> allowed)
    {
        ArgumentNullException.ThrowIfNull(dates);
        if (dates.Count == 0)
        {
            throw new ArgumentException("At least one date is required.", nameof(dates));
        }

        var best = LastDay;
        var bestCount = -1;
        for (var i = dates.Count - 1; i >= 0; i--)
        {
            foreach (var candidate in Candidates(dates[i]))
            {
                if (!allowed(candidate))
                {
                    continue;
                }

                var count = 0;
                foreach (var d in dates)
                {
                    if (OnDay(d.Year, d.Month, candidate) == d)
                    {
                        count++;
                    }
                }

                if (count > bestCount)
                {
                    best = candidate;
                    bestCount = count;
                }
            }
        }

        return best;
    }

    private static IEnumerable<int> Candidates(DateOnly date)
    {
        if (date.Day == 31)
        {
            yield return LastDay;
            yield break;
        }

        yield return date.Day;
        if (IsLastDayOfMonth(date))
        {
            yield return LastDay;
        }
    }

    private static int Effective(int key) => key == LastDay ? 31 : key;

    // Circular distance on a 30-day month, so the 1st and the 31st (last day) count as neighbours.
    private static int SpreadDays(int a, int b)
    {
        var d = Math.Abs(Effective(a) - Effective(b));
        return Math.Min(d, Math.Abs(30 - d));
    }

    private static int Opposite(int key) => key == LastDay ? 15 : key <= 15 ? key + 15 : key - 15;

    private static (int, int) Order(int a, int b) =>
        (a == LastDay ? 32 : a) <= (b == LastDay ? 32 : b) ? (a, b) : (b, a);

    private static DateOnly Nearest(DateOnly ideal, DateOnly after, IReadOnlyList<int> dayKeys)
    {
        DateOnly? best = null;
        var bestDistance = int.MaxValue;
        var month = new DateOnly(ideal.Year, ideal.Month, 1).AddMonths(-1);
        for (var m = 0; m < 3; m++, month = month.AddMonths(1))
        {
            foreach (var key in dayKeys)
            {
                var candidate = OnDay(month.Year, month.Month, key);
                var distance = Math.Abs(candidate.DayNumber - ideal.DayNumber);
                if (candidate > after && (distance < bestDistance || (distance == bestDistance && candidate < best)))
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
        }

        return best ?? ideal;
    }
}
