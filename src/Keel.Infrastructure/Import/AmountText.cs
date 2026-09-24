using System.Globalization;
using Keel.Domain;

namespace Keel.Infrastructure.Import;

/// <summary>
/// Parses amounts as banks write them: currency symbols and codes, thousands separators,
/// decimal comma or point, leading or trailing minus, parentheses for negatives, and
/// <c>CR</c>/<c>DR</c> suffixes (credit positive, debit negative).
/// </summary>
internal static class AmountText
{
    /// <summary>Auto-detect the decimal separator per value.</summary>
    public const char AutoSeparator = '\0';

    /// <summary>Parses to a signed decimal in major units.</summary>
    /// <param name="text">Cell text.</param>
    /// <param name="decimalSeparator"><c>.</c>, <c>,</c> or <see cref="AutoSeparator"/>.</param>
    /// <param name="value">The parsed value.</param>
    /// <returns>False for empty text or text that is not an amount.</returns>
    public static bool TryParse(string? text, char decimalSeparator, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        var first = -1;
        var last = -1;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsAsciiDigit(s[i]))
            {
                first = first < 0 ? i : first;
                last = i;
            }
        }

        if (first < 0)
        {
            return false;
        }

        var prefix = s[..first];
        var core = s[first..(last + 1)];
        var suffix = s[(last + 1)..];

        // A leading ".5" or ",5" belongs to the number.
        if (prefix.EndsWith('.') || prefix.EndsWith(','))
        {
            core = prefix[^1] + core;
            prefix = prefix[..^1];
        }

        if (!IsAffix(prefix) || !IsAffix(suffix))
        {
            return false;
        }

        var upperSuffix = suffix.ToUpperInvariant();
        var negative = prefix.Contains('-') || prefix.Contains('−') || prefix.Contains('–')
            || (prefix.Contains('(') && suffix.Contains(')'))
            || suffix.Contains('-') || suffix.Contains('−')
            || upperSuffix.Contains("DR", StringComparison.Ordinal);

        if (!TryParseCore(core, decimalSeparator, out var magnitude))
        {
            return false;
        }

        value = negative ? -magnitude : magnitude;
        return true;
    }

    /// <summary>Parses to signed minor units of <paramref name="currency"/> (banker's rounding).</summary>
    public static bool TryParseMinor(string? text, char decimalSeparator, string currency, out long minorUnits)
    {
        minorUnits = 0;
        if (!TryParse(text, decimalSeparator, out var value))
        {
            return false;
        }

        try
        {
            minorUnits = Money.FromDecimal(value, currency).Amount;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Guesses the decimal separator of a column: comma when some value ends in a comma followed
    /// by one or two digits, or uses points as thousands separators before a comma.
    /// </summary>
    public static char DetectDecimalSeparator(IEnumerable<string> values)
    {
        var commaVotes = 0;
        var pointVotes = 0;
        foreach (var raw in values)
        {
            var v = raw.Trim().TrimEnd(')', '-', ' ', 'C', 'R', 'D', 'c', 'r', 'd', '€', '$', '£');
            var lastComma = v.LastIndexOf(',');
            var lastPoint = v.LastIndexOf('.');
            if (lastComma >= 0 && lastComma > lastPoint)
            {
                var decimals = v.Length - lastComma - 1;
                if (decimals is 1 or 2 || (lastPoint >= 0 && decimals != 3))
                {
                    commaVotes++;
                }
                else if (lastPoint >= 0)
                {
                    commaVotes++; // 1.234,567: points group, comma is decimal
                }
            }
            else if (lastPoint >= 0)
            {
                var decimals = v.Length - lastPoint - 1;
                if (decimals is 1 or 2 || lastComma >= 0)
                {
                    pointVotes++;
                }
            }
        }

        return commaVotes > pointVotes ? ',' : '.';
    }

    private static bool IsAffix(string affix)
    {
        foreach (var c in affix)
        {
            if (!(char.IsLetter(c) || char.IsWhiteSpace(c) || c is '-' or '+' or '(' or ')' or '−' or '–'
                || char.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseCore(string core, char decimalSeparator, out decimal magnitude)
    {
        magnitude = 0;
        var separator = decimalSeparator == AutoSeparator ? GuessSeparator(core) : decimalSeparator;
        var group = separator == ',' ? '.' : ',';
        Span<char> buffer = stackalloc char[core.Length];
        var length = 0;
        var seenDecimal = false;
        foreach (var c in core)
        {
            if (char.IsAsciiDigit(c))
            {
                buffer[length++] = c;
            }
            else if (c == separator)
            {
                if (seenDecimal)
                {
                    return false;
                }

                seenDecimal = true;
                buffer[length++] = '.';
            }
            else if (c == group || c is '\'' or ' ' or ' ' or ' ' or ' ')
            {
                // thousands separator
            }
            else
            {
                return false;
            }
        }

        return decimal.TryParse(buffer[..length], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out magnitude);
    }

    private static char GuessSeparator(string core)
    {
        var lastComma = core.LastIndexOf(',');
        var lastPoint = core.LastIndexOf('.');
        if (lastComma >= 0 && lastPoint >= 0)
        {
            return lastComma > lastPoint ? ',' : '.';
        }

        if (lastComma >= 0)
        {
            var single = core.IndexOf(',') == lastComma;
            return single && core.Length - lastComma - 1 != 3 ? ',' : '.';
        }

        return '.';
    }
}
