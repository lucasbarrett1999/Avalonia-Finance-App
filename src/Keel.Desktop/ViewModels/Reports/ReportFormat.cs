using System.Globalization;
using System.Text;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Reports;

/// <summary>Text, number and CSV helpers shared by the report view models.</summary>
public static class ReportFormat
{
    /// <summary>Minor units as a double in major units (chart values only; never for math).</summary>
    public static double Major(long amount, string currency) => (double)amount / Currency.MinorUnitsPerMajor(ValidCurrency(currency));

    /// <summary>Currency text, e.g. "$1,234.56".</summary>
    public static string Money(long amount, string currency) => LedgerText.Money(amount, currency);

    /// <summary>Short axis label for an amount in major units, e.g. "$12k".</summary>
    public static string Axis(double major, string currency)
    {
        var symbol = Currency.Symbol(ValidCurrency(currency));
        var abs = Math.Abs(major);
        var sign = major < 0 ? "-" : string.Empty;
        return abs >= 1_000_000 ? string.Create(CultureInfo.CurrentCulture, $"{sign}{symbol}{abs / 1_000_000:0.#}M")
            : abs >= 1_000 ? string.Create(CultureInfo.CurrentCulture, $"{sign}{symbol}{abs / 1_000:0.#}k")
            : string.Create(CultureInfo.CurrentCulture, $"{sign}{symbol}{abs:0}");
    }

    /// <summary>"Jan 2026".</summary>
    public static string MonthLabel(DateOnly month) => month.ToString("MMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>"Jan 26" (chart axis).</summary>
    public static string ShortMonth(DateOnly month) => month.ToString("MMM yy", CultureInfo.CurrentCulture);

    /// <summary>A date range, e.g. "Jan 1, 2026 – Mar 31, 2026".</summary>
    public static string Range(DateOnly from, DateOnly to) =>
        LedgerText.Format(Strings.Reports_RangeFormat, from.ToString("MMM d, yyyy", CultureInfo.CurrentCulture), to.ToString("MMM d, yyyy", CultureInfo.CurrentCulture));

    /// <summary>A share of a total, e.g. "12.5%".</summary>
    public static string Share(long part, long total) =>
        total <= 0 || part <= 0 ? string.Empty : ((double)part / total).ToString("P1", CultureInfo.CurrentCulture);

    /// <summary>Change versus the previous period, e.g. "▲ 12% vs previous" (arrow and words, not colour alone).</summary>
    public static string Change(long current, long previous)
    {
        if (previous == 0)
        {
            return current == 0 ? string.Empty : Strings.Reports_ChangeNew;
        }

        var ratio = (double)(current - previous) / Math.Abs(previous);
        var percent = Math.Abs(ratio).ToString("P0", CultureInfo.CurrentCulture);
        return ratio switch
        {
            > 0.0005 => LedgerText.Format(Strings.Reports_ChangeUp, percent),
            < -0.0005 => LedgerText.Format(Strings.Reports_ChangeDown, percent),
            _ => Strings.Reports_ChangeSame,
        };
    }

    /// <summary>The register search text for a date range (F-TXN-7 <c>date:</c> syntax).</summary>
    public static string DateSearch(DateOnly from, DateOnly to) =>
        string.Create(CultureInfo.InvariantCulture, $"date:{from:yyyy-MM-dd}..{to:yyyy-MM-dd}");

    /// <summary>An amount for CSV: invariant decimal in major units, no symbol (e.g. -1234.50).</summary>
    public static string CsvAmount(long amount, string currency)
    {
        var digits = Currency.MinorUnitDigits(ValidCurrency(currency));
        var value = (decimal)amount / Currency.MinorUnitsPerMajor(ValidCurrency(currency));
        return value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>Builds RFC 4180 CSV text (quotes fields with commas, quotes or line breaks; CRLF line ends).</summary>
    public static string Csv(IEnumerable<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                var field = row[i] ?? string.Empty;
                if (field.AsSpan().IndexOfAny(",\"\r\n") >= 0)
                {
                    builder.Append('"').Append(field.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                }
                else
                {
                    builder.Append(field);
                }
            }

            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    private static string ValidCurrency(string currency) => Currency.IsValidCode(currency) ? currency : Currency.Default;
}
