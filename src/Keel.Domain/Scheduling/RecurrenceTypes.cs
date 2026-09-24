using System.Globalization;

namespace Keel.Domain.Scheduling;

/// <summary>The <c>FREQ</c> values of the supported RFC 5545 subset (F-ACC-6).</summary>
public enum RecurrenceFrequency
{
    /// <summary><c>FREQ=DAILY</c>.</summary>
    Daily,

    /// <summary><c>FREQ=WEEKLY</c>.</summary>
    Weekly,

    /// <summary><c>FREQ=MONTHLY</c>.</summary>
    Monthly,

    /// <summary><c>FREQ=YEARLY</c>.</summary>
    Yearly,
}

/// <summary>
/// One <c>BYDAY</c> entry: a weekday, optionally with an ordinal within the month
/// (<c>2TU</c> = second Tuesday, <c>-1FR</c> = last Friday, <c>MO</c> = every Monday).
/// </summary>
/// <param name="Day">Weekday.</param>
/// <param name="Ordinal">0 for every such weekday; 1 to 5 or -1 to -5 for the nth (from the end) in the month.</param>
public readonly record struct WeekdayOccurrence(DayOfWeek Day, int Ordinal = 0) : IComparable<WeekdayOccurrence>
{
    /// <summary>The two-letter RFC 5545 code of a weekday (<c>MO</c> … <c>SU</c>).</summary>
    public static string Code(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        DayOfWeek.Saturday => "SA",
        DayOfWeek.Sunday => "SU",
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, "Unknown weekday."),
    };

    /// <summary>Parses a two-letter weekday code (case-insensitive).</summary>
    public static bool TryParseCode(ReadOnlySpan<char> code, out DayOfWeek day)
    {
        day = default;
        if (code.Length != 2)
        {
            return false;
        }

        Span<char> upper = stackalloc char[2];
        code.ToUpperInvariant(upper);
        switch (upper)
        {
            case "MO": day = DayOfWeek.Monday; return true;
            case "TU": day = DayOfWeek.Tuesday; return true;
            case "WE": day = DayOfWeek.Wednesday; return true;
            case "TH": day = DayOfWeek.Thursday; return true;
            case "FR": day = DayOfWeek.Friday; return true;
            case "SA": day = DayOfWeek.Saturday; return true;
            case "SU": day = DayOfWeek.Sunday; return true;
            default: return false;
        }
    }

    /// <summary>Monday-first index (Monday 0 … Sunday 6), used for canonical ordering.</summary>
    public static int MondayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    /// <inheritdoc />
    public int CompareTo(WeekdayOccurrence other)
    {
        var byOrdinal = SortKey(Ordinal).CompareTo(SortKey(other.Ordinal));
        return byOrdinal != 0 ? byOrdinal : MondayIndex(Day).CompareTo(MondayIndex(other.Day));
    }

    /// <summary>RFC 5545 form, e.g. <c>2TU</c>, <c>-1FR</c>, <c>MO</c>.</summary>
    public override string ToString() =>
        Ordinal == 0 ? Code(Day) : Ordinal.ToString(CultureInfo.InvariantCulture) + Code(Day);

    // Every-weekday entries first, then 1..5, then -5..-1 (month order).
    private static int SortKey(int ordinal) => ordinal switch
    {
        0 => 0,
        > 0 => ordinal,
        _ => 11 + ordinal,
    };
}

/// <summary>A recurrence rule string could not be parsed; the message says which part is wrong.</summary>
public sealed class RecurrenceRuleFormatException : FormatException
{
    /// <summary>Creates the exception.</summary>
    public RecurrenceRuleFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public RecurrenceRuleFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public RecurrenceRuleFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
