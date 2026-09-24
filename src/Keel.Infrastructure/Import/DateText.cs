using System.Globalization;
using System.Text.RegularExpressions;
using Keel.Application.Import;

namespace Keel.Infrastructure.Import;

/// <summary>A built-in date format: a display name, a shape check and the .NET patterns that read it.</summary>
/// <param name="Name">Canonical name stored in <see cref="CsvColumnMapping.DateFormat"/>.</param>
/// <param name="Shape">Regular expression the whole value must match.</param>
/// <param name="Patterns">Exact .NET patterns tried in order.</param>
/// <param name="Order">Numeric day/month order, or null when the format is unambiguous.</param>
internal sealed record DateFormatSpec(string Name, Regex Shape, string[] Patterns, DateOrder? Order);

/// <summary>Result of detecting the date format of a column.</summary>
/// <param name="Candidates">Formats that parse every value, best first.</param>
/// <param name="IsAmbiguous">Month-first and day-first both fit and give different dates.</param>
internal sealed record DateDetection(IReadOnlyList<DateFormatSpec> Candidates, bool IsAmbiguous)
{
    public DateFormatSpec? Best => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>
/// Date formats found in bank exports: ISO, US and European numeric forms with 4- or 2-digit
/// years, compact <c>yyyyMMdd</c>, and month names (<c>Jan 5, 2026</c>, <c>05-Jan-2026</c>).
/// A trailing time of day is ignored.
/// </summary>
internal static partial class DateText
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    /// <summary>All built-in formats, in preference order.</summary>
    public static IReadOnlyList<DateFormatSpec> Formats { get; } =
    [
        new("yyyy-MM-dd", new(@"^\d{4}[-/.]\d{1,2}[-/.]\d{1,2}$", Options), ["yyyy-M-d", "yyyy/M/d", "yyyy.M.d"], null),
        new("yyyyMMdd", new(@"^\d{8}$", Options), ["yyyyMMdd"], null),
        new("MM/dd/yyyy", new(@"^\d{1,2}[-/.]\d{1,2}[-/.]\d{4}$", Options), ["M/d/yyyy", "M-d-yyyy", "M.d.yyyy"], DateOrder.MonthFirst),
        new("dd/MM/yyyy", new(@"^\d{1,2}[-/.]\d{1,2}[-/.]\d{4}$", Options), ["d/M/yyyy", "d-M-yyyy", "d.M.yyyy"], DateOrder.DayFirst),
        new("MM/dd/yy", new(@"^\d{1,2}[-/.]\d{1,2}[-/.]\d{2}$", Options), ["M/d/yy", "M-d-yy", "M.d.yy"], DateOrder.MonthFirst),
        new("dd/MM/yy", new(@"^\d{1,2}[-/.]\d{1,2}[-/.]\d{2}$", Options), ["d/M/yy", "d-M-yy", "d.M.yy"], DateOrder.DayFirst),
        new("MMM d, yyyy", new(@"^[a-z]{3,9}\.?\s+\d{1,2},?\s+\d{4}$", Options),
            ["MMM d, yyyy", "MMM d yyyy", "MMMM d, yyyy", "MMMM d yyyy", "MMM. d, yyyy"], null),
        new("d MMM yyyy", new(@"^\d{1,2}[-\s][a-z]{3,9}\.?[-\s]\d{2,4}$", Options),
            ["d MMM yyyy", "d-MMM-yyyy", "d MMMM yyyy", "d-MMMM-yyyy", "d-MMM-yy", "d MMM yy"], null),
    ];

    /// <summary>Removes a trailing time of day (<c>2026-01-05T00:00:00</c>, <c>01/05/2026 12:00 AM</c>).</summary>
    public static string StripTime(string value)
    {
        var v = value.Trim();
        var m = TrailingTime().Match(v);
        return m.Success ? m.Groups[1].Value.Trim() : v;
    }

    /// <summary>Finds a built-in format by name.</summary>
    public static DateFormatSpec? Find(string name) =>
        Formats.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));

    /// <summary>Parses with a built-in format or, failing that, a custom .NET exact format.</summary>
    public static bool TryParse(string? text, string format, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = StripTime(text);
        if (Find(format) is { } spec)
        {
            return TryParse(value, spec, out date);
        }

        return DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    /// <summary>Parses with a built-in format.</summary>
    public static bool TryParse(string value, DateFormatSpec spec, out DateOnly date)
    {
        date = default;
        var v = StripTime(value);
        return spec.Shape.IsMatch(v)
            && DateOnly.TryParseExact(v, spec.Patterns, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    /// <summary>True when some built-in format reads the value.</summary>
    public static bool LooksLikeDate(string value) =>
        !string.IsNullOrWhiteSpace(value) && Formats.Any(f => TryParse(value, f, out _));

    /// <summary>
    /// Detects the formats that read every non-empty value. When both a month-first and a
    /// day-first format fit, the preferred order goes first; the result is ambiguous unless both
    /// read every value as the same date.
    /// </summary>
    public static DateDetection Detect(IReadOnlyCollection<string> values, DateOrder? preferred)
    {
        var nonEmpty = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (nonEmpty.Count == 0)
        {
            return new DateDetection([], false);
        }

        var fits = Formats.Where(f => nonEmpty.All(v => TryParse(v, f, out _))).ToList();
        var monthFirst = fits.FirstOrDefault(f => f.Order == DateOrder.MonthFirst);
        var dayFirst = fits.FirstOrDefault(f => f.Order == DateOrder.DayFirst);
        var ambiguous = false;
        if (monthFirst is not null && dayFirst is not null)
        {
            ambiguous = nonEmpty.Any(v =>
                TryParse(v, monthFirst, out var a) && TryParse(v, dayFirst, out var b) && a != b);
            if (!ambiguous || preferred is not null)
            {
                // Equal readings, or the user has answered: keep only the preferred order.
                var drop = (preferred ?? DateOrder.MonthFirst) == DateOrder.MonthFirst ? DateOrder.DayFirst : DateOrder.MonthFirst;
                fits = fits.Where(f => f.Order != drop).ToList();
                ambiguous = false;
            }
        }

        return new DateDetection(fits, ambiguous);
    }

    [GeneratedRegex(@"^(.+?)(?:[T\s]\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?\s*(?:[AP]M)?\s*(?:Z|[+-]\d{2}:?\d{2})?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TrailingTime();
}
