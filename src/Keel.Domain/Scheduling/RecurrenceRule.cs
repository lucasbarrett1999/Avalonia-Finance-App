using System.Globalization;
using System.Text;

namespace Keel.Domain.Scheduling;

/// <summary>
/// A recurrence rule in the RFC 5545 <c>RRULE</c> subset of F-ACC-6: daily, weekly or every N weeks,
/// monthly on day D (including the last day) or on the Nth weekday, twice monthly, yearly, with
/// <c>COUNT</c> or <c>UNTIL</c>. Immutable; equality is by the canonical string form.
/// </summary>
/// <remarks>
/// <para>Supported parts: <c>FREQ</c> (DAILY, WEEKLY, MONTHLY, YEARLY), <c>INTERVAL</c>,
/// <c>BYDAY</c>, <c>BYMONTHDAY</c>, <c>BYMONTH</c>, <c>COUNT</c>, <c>UNTIL</c>, <c>WKST</c>.
/// Anything else is rejected with a message naming the part. Interpretations (see
/// <c>docs/decisions/0030-recurrence-rule-subset.md</c>):</para>
/// <list type="bullet">
/// <item>The start date plays the role of <c>DTSTART</c>: it supplies the weekday, day of month
/// and month when the rule does not name them, and <c>COUNT</c> is counted from it. The start date
/// is an occurrence only if it matches the rule.</item>
/// <item>A day of month that does not exist in a month is clamped to the last day (the 31st is the
/// 30th in April and the 28th or 29th in February; February 29 yearly is February 28 in common
/// years). RFC 5545 would skip such months; a bill does not skip a month.</item>
/// <item>An ordinal weekday that does not exist (a fifth Friday) is skipped for that month.</item>
/// <item>Dates are <see cref="DateOnly"/>: no time zone, no DST. <c>UNTIL</c> with a time keeps
/// only the date.</item>
/// </list>
/// </remarks>
public sealed class RecurrenceRule : IEquatable<RecurrenceRule>
{
    /// <summary>Largest accepted <c>INTERVAL</c>.</summary>
    public const int MaxInterval = 999;

    private readonly string _canonical;

    private RecurrenceRule(
        RecurrenceFrequency frequency,
        int interval,
        IReadOnlyList<WeekdayOccurrence> byDay,
        IReadOnlyList<int> byMonthDay,
        IReadOnlyList<int> byMonth,
        int? count,
        DateOnly? until,
        DayOfWeek weekStart)
    {
        Frequency = frequency;
        Interval = interval;
        ByDay = byDay;
        ByMonthDay = byMonthDay;
        ByMonth = byMonth;
        Count = count;
        Until = until;
        WeekStart = weekStart;
        _canonical = BuildCanonical();
    }

    /// <summary>Frequency (<c>FREQ</c>).</summary>
    public RecurrenceFrequency Frequency { get; }

    /// <summary>Every how many periods (<c>INTERVAL</c>, at least 1).</summary>
    public int Interval { get; }

    /// <summary><c>BYDAY</c> entries in canonical order; empty when not given.</summary>
    public IReadOnlyList<WeekdayOccurrence> ByDay { get; }

    /// <summary><c>BYMONTHDAY</c> values (1..31, or -1..-31 counted from the month end) in canonical order.</summary>
    public IReadOnlyList<int> ByMonthDay { get; }

    /// <summary><c>BYMONTH</c> values (1..12), ascending.</summary>
    public IReadOnlyList<int> ByMonth { get; }

    /// <summary>Total number of occurrences counted from the start date (<c>COUNT</c>).</summary>
    public int? Count { get; }

    /// <summary>Last possible occurrence date, inclusive (<c>UNTIL</c>).</summary>
    public DateOnly? Until { get; }

    /// <summary>First day of the week for <c>WEEKLY</c> with <c>INTERVAL</c> &gt; 1 (<c>WKST</c>, default Monday).</summary>
    public DayOfWeek WeekStart { get; }

    // ---------------------------------------------------------------------------------------------
    // Construction

    /// <summary>
    /// Creates a validated rule.
    /// </summary>
    /// <exception cref="ArgumentException">The combination is outside the supported subset.</exception>
    public static RecurrenceRule Create(
        RecurrenceFrequency frequency,
        int interval = 1,
        IEnumerable<WeekdayOccurrence>? byDay = null,
        IEnumerable<int>? byMonthDay = null,
        IEnumerable<int>? byMonth = null,
        int? count = null,
        DateOnly? until = null,
        DayOfWeek weekStart = DayOfWeek.Monday)
    {
        var days = (byDay ?? []).ToList();
        var monthDays = (byMonthDay ?? []).ToList();
        var months = (byMonth ?? []).ToList();
        var error = Validate(frequency, interval, days, monthDays, months, count, until);
        if (error is not null)
        {
            throw new ArgumentException(error);
        }

        return new RecurrenceRule(
            frequency,
            interval,
            days.Distinct().Order().ToArray(),
            monthDays.Distinct().OrderBy(MonthDaySortKey).ToArray(),
            months.Distinct().Order().ToArray(),
            count,
            until,
            weekStart);
    }

    /// <summary><c>FREQ=DAILY</c>, every <paramref name="interval"/> days.</summary>
    public static RecurrenceRule Daily(int interval = 1) => Create(RecurrenceFrequency.Daily, interval);

    /// <summary><c>FREQ=WEEKLY;BYDAY=…</c>, every <paramref name="interval"/> weeks.</summary>
    public static RecurrenceRule Weekly(DayOfWeek day, int interval = 1) =>
        Create(RecurrenceFrequency.Weekly, interval, [new WeekdayOccurrence(day)]);

    /// <summary><c>FREQ=MONTHLY;BYMONTHDAY=…</c>; use -1 for the last day of the month.</summary>
    public static RecurrenceRule MonthlyOnDay(int dayOfMonth, int interval = 1) =>
        Create(RecurrenceFrequency.Monthly, interval, byMonthDay: [dayOfMonth]);

    /// <summary>Twice monthly, <c>FREQ=MONTHLY;BYMONTHDAY=a,b</c> (e.g. 1 and 15, or 15 and -1).</summary>
    public static RecurrenceRule TwiceMonthly(int firstDay, int secondDay) =>
        Create(RecurrenceFrequency.Monthly, 1, byMonthDay: [firstDay, secondDay]);

    /// <summary><c>FREQ=MONTHLY;BYDAY=nDD</c>: the nth (or, negative, nth-from-last) weekday of the month.</summary>
    public static RecurrenceRule MonthlyOnWeekday(int ordinal, DayOfWeek day, int interval = 1) =>
        Create(RecurrenceFrequency.Monthly, interval, [new WeekdayOccurrence(day, ordinal)]);

    /// <summary><c>FREQ=YEARLY;BYMONTH=m;BYMONTHDAY=d</c>.</summary>
    public static RecurrenceRule Yearly(int month, int dayOfMonth, int interval = 1) =>
        Create(RecurrenceFrequency.Yearly, interval, byMonthDay: [dayOfMonth], byMonth: [month]);

    /// <summary>This rule with <c>COUNT</c> set (and <c>UNTIL</c> cleared), or cleared when null.</summary>
    public RecurrenceRule WithCount(int? count) =>
        Create(Frequency, Interval, ByDay, ByMonthDay, ByMonth, count, count is null ? Until : null, WeekStart);

    /// <summary>This rule with <c>UNTIL</c> set (and <c>COUNT</c> cleared), or cleared when null.</summary>
    public RecurrenceRule WithUntil(DateOnly? until) =>
        Create(Frequency, Interval, ByDay, ByMonthDay, ByMonth, until is null ? Count : null, until, WeekStart);

    // ---------------------------------------------------------------------------------------------
    // Parsing

    /// <summary>
    /// Parses a rule such as <c>FREQ=MONTHLY;BYDAY=2TU</c>. An <c>RRULE:</c> prefix, lower case and a
    /// trailing semicolon are accepted.
    /// </summary>
    /// <exception cref="RecurrenceRuleFormatException">The text is not a rule in the supported subset.</exception>
    public static RecurrenceRule Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out var rule, out var error) ? rule : throw new RecurrenceRuleFormatException(error);
    }

    /// <summary>Parses a rule; on failure returns false with a message naming the offending part.</summary>
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RecurrenceRule? rule, out string error)
    {
        rule = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The recurrence rule is empty.";
            return false;
        }

        var body = text.Trim();
        if (body.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            body = body[6..];
        }

        RecurrenceFrequency? frequency = null;
        var interval = 1;
        List<WeekdayOccurrence> byDay = [];
        List<int> byMonthDay = [];
        List<int> byMonth = [];
        int? count = null;
        DateOnly? until = null;
        var weekStart = DayOfWeek.Monday;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawPart in body.Split(';'))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                error = $"'{part}' is not a NAME=VALUE rule part.";
                return false;
            }

            var name = part[..eq].Trim().ToUpperInvariant();
            var value = part[(eq + 1)..].Trim();
            if (!seen.Add(name))
            {
                error = $"{name} appears more than once.";
                return false;
            }

            if (value.Length == 0)
            {
                error = $"{name} has no value.";
                return false;
            }

            switch (name)
            {
                case "FREQ":
                    frequency = value.ToUpperInvariant() switch
                    {
                        "DAILY" => RecurrenceFrequency.Daily,
                        "WEEKLY" => RecurrenceFrequency.Weekly,
                        "MONTHLY" => RecurrenceFrequency.Monthly,
                        "YEARLY" => RecurrenceFrequency.Yearly,
                        _ => null,
                    };
                    if (frequency is null)
                    {
                        error = value.ToUpperInvariant() is "HOURLY" or "MINUTELY" or "SECONDLY"
                            ? $"FREQ={value.ToUpperInvariant()} is not supported; use DAILY, WEEKLY, MONTHLY or YEARLY."
                            : $"FREQ '{value}' is not valid; use DAILY, WEEKLY, MONTHLY or YEARLY.";
                        return false;
                    }

                    break;

                case "INTERVAL":
                    if (!TryParseInt(value, out interval) || interval < 1 || interval > MaxInterval)
                    {
                        error = $"INTERVAL '{value}' must be a whole number from 1 to {MaxInterval}.";
                        return false;
                    }

                    break;

                case "COUNT":
                    if (!TryParseInt(value, out var c) || c < 1)
                    {
                        error = $"COUNT '{value}' must be a whole number of at least 1.";
                        return false;
                    }

                    count = c;
                    break;

                case "UNTIL":
                    if (!TryParseUntil(value, out var u))
                    {
                        error = $"UNTIL '{value}' must be a date like 20271231 (or 20271231T235959Z).";
                        return false;
                    }

                    until = u;
                    break;

                case "WKST":
                    if (!WeekdayOccurrence.TryParseCode(value, out weekStart))
                    {
                        error = $"WKST '{value}' must be a weekday code: MO, TU, WE, TH, FR, SA or SU.";
                        return false;
                    }

                    break;

                case "BYDAY":
                    foreach (var item in value.Split(','))
                    {
                        if (!TryParseWeekday(item.Trim(), out var wd))
                        {
                            error = $"BYDAY entry '{item.Trim()}' must be a weekday code (MO … SU) with an optional ordinal from -5 to 5, such as 2TU or -1FR.";
                            return false;
                        }

                        byDay.Add(wd);
                    }

                    break;

                case "BYMONTHDAY":
                    foreach (var item in value.Split(','))
                    {
                        if (!TryParseInt(item.Trim(), out var md) || md == 0 || md < -31 || md > 31)
                        {
                            error = $"BYMONTHDAY entry '{item.Trim()}' must be 1 to 31, or -1 to -31 counted from the end of the month.";
                            return false;
                        }

                        byMonthDay.Add(md);
                    }

                    break;

                case "BYMONTH":
                    foreach (var item in value.Split(','))
                    {
                        if (!TryParseInt(item.Trim(), out var m) || m < 1 || m > 12)
                        {
                            error = $"BYMONTH entry '{item.Trim()}' must be a month number from 1 to 12.";
                            return false;
                        }

                        byMonth.Add(m);
                    }

                    break;

                case "BYSETPOS" or "BYWEEKNO" or "BYYEARDAY" or "BYHOUR" or "BYMINUTE" or "BYSECOND":
                    error = $"{name} is not supported by Keel's recurrence rules.";
                    return false;

                default:
                    error = $"Unknown rule part '{name}'.";
                    return false;
            }
        }

        if (frequency is null)
        {
            error = "FREQ is required (DAILY, WEEKLY, MONTHLY or YEARLY).";
            return false;
        }

        var validation = Validate(frequency.Value, interval, byDay, byMonthDay, byMonth, count, until);
        if (validation is not null)
        {
            error = validation;
            return false;
        }

        rule = Create(frequency.Value, interval, byDay, byMonthDay, byMonth, count, until, weekStart);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Occurrences

    /// <summary>
    /// The occurrences of this rule for a schedule that starts on <paramref name="start"/>, restricted
    /// to <paramref name="from"/> … <paramref name="to"/> (inclusive), strictly increasing.
    /// <c>COUNT</c> counts occurrences from <paramref name="start"/>, including those before
    /// <paramref name="from"/>.
    /// </summary>
    public IEnumerable<DateOnly> Occurrences(DateOnly start, DateOnly from, DateOnly to)
    {
        if (to < from || to < start)
        {
            yield break;
        }

        var emitted = 0;
        var firstPeriod = Count is null ? FirstUsefulPeriod(start, from) : 0;
        var candidates = new List<DateOnly>(8);
        var last = DateOnly.MinValue;
        var any = false;
        // Terminates: period starts grow until they pass `to`, UNTIL or year 9999.
        for (var period = firstPeriod; ; period++)
        {
            if (!TryPeriodStart(start, period, out var periodStart) || periodStart > to || (Until is { } u0 && periodStart > u0))
            {
                yield break;
            }

            candidates.Clear();
            AddCandidates(start, period, candidates);
            candidates.Sort();
            foreach (var date in candidates)
            {
                if (date < start || (any && date <= last))
                {
                    continue; // before DTSTART, or a duplicate produced by clamping
                }

                if ((Until is { } u && date > u) || date > to)
                {
                    yield break;
                }

                emitted++;
                any = true;
                last = date;
                if (date >= from)
                {
                    yield return date;
                }

                if (Count is { } c && emitted >= c)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// The first occurrence strictly after <paramref name="after"/> for a schedule starting on
    /// <paramref name="start"/>, or null when the rule has ended (<c>COUNT</c> or <c>UNTIL</c>).
    /// </summary>
    public DateOnly? NextAfter(DateOnly start, DateOnly after)
    {
        if (after >= DateOnly.MaxValue)
        {
            return null;
        }

        foreach (var date in Occurrences(start, after.AddDays(1), DateOnly.MaxValue))
        {
            return date;
        }

        return null;
    }

    /// <summary>The first occurrence on or after <paramref name="start"/>, or null if there is none.</summary>
    public DateOnly? First(DateOnly start)
    {
        foreach (var date in Occurrences(start, start, DateOnly.MaxValue))
        {
            return date;
        }

        return null;
    }

    private long FirstUsefulPeriod(DateOnly start, DateOnly from)
    {
        if (from <= start)
        {
            return 0;
        }

        // The period that contains (or precedes) `from`; earlier periods cannot contribute.
        long periods = Frequency switch
        {
            RecurrenceFrequency.Daily => (from.DayNumber - start.DayNumber) / Interval,
            RecurrenceFrequency.Weekly => (from.DayNumber - WeekAnchor(start).DayNumber) / (7L * Interval),
            RecurrenceFrequency.Monthly => (MonthIndex(from) - MonthIndex(start)) / Interval,
            RecurrenceFrequency.Yearly => (from.Year - start.Year) / Interval,
            _ => 0,
        };
        return Math.Max(0, periods - 1);
    }

    private bool TryPeriodStart(DateOnly start, long period, out DateOnly periodStart)
    {
        periodStart = default;
        switch (Frequency)
        {
            case RecurrenceFrequency.Daily:
                return TryFromDayNumber(start.DayNumber + (period * Interval), out periodStart);
            case RecurrenceFrequency.Weekly:
                return TryFromDayNumber(WeekAnchor(start).DayNumber + (period * Interval * 7), out periodStart);
            case RecurrenceFrequency.Monthly:
                {
                    var index = MonthIndex(start) + (period * Interval);
                    if (index / 12 > 9999)
                    {
                        return false;
                    }

                    periodStart = new DateOnly((int)(index / 12), (int)(index % 12) + 1, 1);
                    return true;
                }

            case RecurrenceFrequency.Yearly:
                {
                    var year = start.Year + (period * Interval);
                    if (year > 9999)
                    {
                        return false;
                    }

                    periodStart = new DateOnly((int)year, 1, 1);
                    return true;
                }

            default:
                return false;
        }
    }

    private void AddCandidates(DateOnly start, long period, List<DateOnly> into)
    {
        switch (Frequency)
        {
            case RecurrenceFrequency.Daily:
                {
                    var date = DateOnly.FromDayNumber((int)(start.DayNumber + (period * Interval)));
                    if (ByDay.Count == 0 || ByDay.Any(d => d.Day == date.DayOfWeek))
                    {
                        into.Add(date);
                    }

                    break;
                }

            case RecurrenceFrequency.Weekly:
                {
                    var weekStartDay = WeekAnchor(start).DayNumber + (period * Interval * 7);
                    if (ByDay.Count == 0)
                    {
                        AddIfValid(weekStartDay + DaysFromWeekStart(start.DayOfWeek), into);
                    }
                    else
                    {
                        foreach (var d in ByDay)
                        {
                            AddIfValid(weekStartDay + DaysFromWeekStart(d.Day), into);
                        }
                    }

                    break;
                }

            case RecurrenceFrequency.Monthly:
                {
                    var index = MonthIndex(start) + (period * Interval);
                    AddMonthCandidates((int)(index / 12), (int)(index % 12) + 1, start, into);
                    break;
                }

            case RecurrenceFrequency.Yearly:
                {
                    var year = (int)(start.Year + (period * Interval));
                    if (ByMonth.Count == 0)
                    {
                        AddMonthCandidates(year, start.Month, start, into);
                    }
                    else
                    {
                        foreach (var month in ByMonth)
                        {
                            AddMonthCandidates(year, month, start, into);
                        }
                    }

                    break;
                }
        }
    }

    private void AddMonthCandidates(int year, int month, DateOnly start, List<DateOnly> into)
    {
        var daysInMonth = DateTime.DaysInMonth(year, month);
        if (ByMonthDay.Count == 0 && ByDay.Count == 0)
        {
            into.Add(new DateOnly(year, month, Math.Min(start.Day, daysInMonth)));
            return;
        }

        foreach (var md in ByMonthDay)
        {
            var day = md > 0 ? Math.Min(md, daysInMonth) : Math.Max(1, daysInMonth + md + 1);
            into.Add(new DateOnly(year, month, day));
        }

        if (ByDay.Count == 0)
        {
            return;
        }

        var firstOfMonth = new DateOnly(year, month, 1);
        foreach (var wd in ByDay)
        {
            var firstDay = 1 + ((((int)wd.Day - (int)firstOfMonth.DayOfWeek) + 7) % 7);
            if (wd.Ordinal == 0)
            {
                for (var d = firstDay; d <= daysInMonth; d += 7)
                {
                    into.Add(new DateOnly(year, month, d));
                }
            }
            else if (wd.Ordinal > 0)
            {
                var d = firstDay + (7 * (wd.Ordinal - 1));
                if (d <= daysInMonth)
                {
                    into.Add(new DateOnly(year, month, d));
                }
            }
            else
            {
                var lastDay = firstDay + (7 * ((daysInMonth - firstDay) / 7));
                var d = lastDay + (7 * (wd.Ordinal + 1));
                if (d >= 1)
                {
                    into.Add(new DateOnly(year, month, d));
                }
            }
        }
    }

    private DateOnly WeekAnchor(DateOnly start) => start.AddDays(-DaysFromWeekStart(start.DayOfWeek));

    private int DaysFromWeekStart(DayOfWeek day) => (((int)day - (int)WeekStart) + 7) % 7;

    private static long MonthIndex(DateOnly date) => (date.Year * 12L) + date.Month - 1;

    private static bool TryFromDayNumber(long dayNumber, out DateOnly date)
    {
        date = default;
        if (dayNumber > DateOnly.MaxValue.DayNumber)
        {
            return false;
        }

        date = DateOnly.FromDayNumber((int)dayNumber);
        return true;
    }

    private static void AddIfValid(long dayNumber, List<DateOnly> into)
    {
        if (TryFromDayNumber(dayNumber, out var date))
        {
            into.Add(date);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Text

    /// <summary>The canonical RFC 5545 form, e.g. <c>FREQ=WEEKLY;INTERVAL=2;BYDAY=FR</c>.</summary>
    public override string ToString() => _canonical;

    /// <summary>
    /// A human-readable English description, e.g. "Every 2 weeks on Friday" or "Every month on the
    /// 2nd Tuesday". With <paramref name="start"/>, values the rule takes from the start date are
    /// spelled out ("Every month on the 15th" for <c>FREQ=MONTHLY</c> starting on the 15th).
    /// </summary>
    public string Describe(DateOnly? start = null) => RecurrenceDescriber.Describe(this, start);

    /// <inheritdoc />
    public bool Equals(RecurrenceRule? other) =>
        other is not null && string.Equals(_canonical, other._canonical, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RecurrenceRule);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_canonical);

    /// <summary>Equality by canonical form.</summary>
    public static bool operator ==(RecurrenceRule? left, RecurrenceRule? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Inequality by canonical form.</summary>
    public static bool operator !=(RecurrenceRule? left, RecurrenceRule? right) => !(left == right);

    private string BuildCanonical()
    {
        var sb = new StringBuilder("FREQ=");
        sb.Append(Frequency.ToString().ToUpperInvariant());
        if (Interval != 1)
        {
            sb.Append(CultureInfo.InvariantCulture, $";INTERVAL={Interval}");
        }

        if (ByMonth.Count > 0)
        {
            sb.Append(";BYMONTH=").AppendJoin(',', ByMonth.Select(m => m.ToString(CultureInfo.InvariantCulture)));
        }

        if (ByMonthDay.Count > 0)
        {
            sb.Append(";BYMONTHDAY=").AppendJoin(',', ByMonthDay.Select(d => d.ToString(CultureInfo.InvariantCulture)));
        }

        if (ByDay.Count > 0)
        {
            sb.Append(";BYDAY=").AppendJoin(',', ByDay.Select(d => d.ToString()));
        }

        if (WeekStart != DayOfWeek.Monday)
        {
            sb.Append(";WKST=").Append(WeekdayOccurrence.Code(WeekStart));
        }

        if (Count is { } count)
        {
            sb.Append(CultureInfo.InvariantCulture, $";COUNT={count}");
        }

        if (Until is { } until)
        {
            sb.Append(";UNTIL=").Append(until.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------
    // Validation helpers

    private static string? Validate(
        RecurrenceFrequency frequency,
        int interval,
        IReadOnlyList<WeekdayOccurrence> byDay,
        IReadOnlyList<int> byMonthDay,
        IReadOnlyList<int> byMonth,
        int? count,
        DateOnly? until)
    {
        if (!Enum.IsDefined(frequency))
        {
            return "Unknown frequency.";
        }

        if (interval < 1 || interval > MaxInterval)
        {
            return $"INTERVAL must be from 1 to {MaxInterval}.";
        }

        if (count is not null && until is not null)
        {
            return "COUNT and UNTIL cannot be used together.";
        }

        if (count is < 1)
        {
            return "COUNT must be at least 1.";
        }

        if (byDay.Any(d => d.Ordinal is < -5 or > 5 || !Enum.IsDefined(d.Day)))
        {
            return "BYDAY ordinals must be from -5 to 5.";
        }

        if (byMonthDay.Any(d => d == 0 || d is < -31 or > 31))
        {
            return "BYMONTHDAY values must be 1 to 31 or -1 to -31.";
        }

        if (byMonth.Any(m => m is < 1 or > 12))
        {
            return "BYMONTH values must be 1 to 12.";
        }

        if (byDay.Count > 0 && byMonthDay.Count > 0)
        {
            return "BYDAY and BYMONTHDAY cannot be combined; use one of them.";
        }

        switch (frequency)
        {
            case RecurrenceFrequency.Daily:
                if (byMonthDay.Count > 0 || byMonth.Count > 0)
                {
                    return "FREQ=DAILY supports only BYDAY (plain weekdays), not BYMONTHDAY or BYMONTH.";
                }

                if (byDay.Any(d => d.Ordinal != 0))
                {
                    return "FREQ=DAILY takes plain weekdays in BYDAY (MO, TU, …), without ordinals.";
                }

                break;

            case RecurrenceFrequency.Weekly:
                if (byMonthDay.Count > 0 || byMonth.Count > 0)
                {
                    return "FREQ=WEEKLY supports only BYDAY, not BYMONTHDAY or BYMONTH.";
                }

                if (byDay.Any(d => d.Ordinal != 0))
                {
                    return "FREQ=WEEKLY takes plain weekdays in BYDAY (MO, TU, …); ordinals such as 2TU need FREQ=MONTHLY.";
                }

                break;

            case RecurrenceFrequency.Monthly:
                if (byMonth.Count > 0)
                {
                    return "BYMONTH is supported only with FREQ=YEARLY.";
                }

                break;

            case RecurrenceFrequency.Yearly:
                if (byDay.Count > 0 && byMonth.Count == 0)
                {
                    return "FREQ=YEARLY with BYDAY needs BYMONTH (for example BYMONTH=11;BYDAY=4TH).";
                }

                if (byMonthDay.Count > 0 && byMonth.Count == 0)
                {
                    return "FREQ=YEARLY with BYMONTHDAY needs BYMONTH (for example BYMONTH=3;BYMONTHDAY=1).";
                }

                break;
        }

        return null;
    }

    private static int MonthDaySortKey(int day) => day > 0 ? day : 100 + (32 + day);

    private static bool TryParseInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static bool TryParseWeekday(string text, out WeekdayOccurrence value)
    {
        value = default;
        if (text.Length < 2 || !WeekdayOccurrence.TryParseCode(text.AsSpan(text.Length - 2), out var day))
        {
            return false;
        }

        var ordinalText = text[..^2];
        var ordinal = 0;
        if (ordinalText.Length > 0 && (!TryParseInt(ordinalText, out ordinal) || ordinal == 0 || ordinal is < -5 or > 5))
        {
            return false;
        }

        value = new WeekdayOccurrence(day, ordinal);
        return true;
    }

    private static bool TryParseUntil(string text, out DateOnly date)
    {
        date = default;
        var datePart = text.Length > 8 && (text[8] == 'T' || text[8] == 't') ? text[..8] : text;
        if (datePart.Length != 8 || !datePart.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (datePart.Length != text.Length)
        {
            var time = text[9..].TrimEnd('Z', 'z');
            if (time.Length != 6 || !time.All(char.IsAsciiDigit))
            {
                return false;
            }
        }

        return DateOnly.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
