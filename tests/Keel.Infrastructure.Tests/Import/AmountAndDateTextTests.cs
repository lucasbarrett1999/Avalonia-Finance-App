using System.Globalization;
using Keel.Application.Import;
using Keel.Infrastructure.Import;

namespace Keel.Infrastructure.Tests.Import;

public class AmountAndDateTextTests
{
    [Theory]
    [InlineData("12.34", '.', "12.34")]
    [InlineData("$1,234.56", '.', "1234.56")]
    [InlineData("-$5.00", '.', "-5.00")]
    [InlineData("$-5.00", '.', "-5.00")]
    [InlineData("(12.00)", '.', "-12.00")]
    [InlineData("($250.00)", '.', "-250.00")]
    [InlineData("12.00-", '.', "-12.00")]
    [InlineData("12.00 CR", '.', "12.00")]
    [InlineData("12.00CR", '.', "12.00")]
    [InlineData("12.00 DR", '.', "-12.00")]
    [InlineData("12.00 dr", '.', "-12.00")]
    [InlineData("+3.00", '.', "3.00")]
    [InlineData("−7.25", '.', "-7.25")]
    [InlineData("USD 10.00", '.', "10.00")]
    [InlineData("10.00 EUR", '.', "10.00")]
    [InlineData("€ 1.234,56", ',', "1234.56")]
    [InlineData("-1.150,00", ',', "-1150.00")]
    [InlineData("1 234,56", ',', "1234.56")]
    [InlineData("1'234.56", '.', "1234.56")]
    [InlineData(".5", '.', "0.5")]
    [InlineData("1,234.56", '\0', "1234.56")]
    [InlineData("1.234,56", '\0', "1234.56")]
    [InlineData("12,34", '\0', "12.34")]
    [InlineData("1,234", '\0', "1234")]
    [InlineData("-42.17", '\0', "-42.17")]
    public void Parses_bank_amount_formats(string text, char separator, string expected)
    {
        AmountText.TryParse(text, separator, out var value).ShouldBeTrue();
        value.ShouldBe(decimal.Parse(expected, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12a34")]
    [InlineData("1.2.3")]
    [InlineData("12/05/2026")]
    public void Rejects_non_amounts(string? text)
    {
        AmountText.TryParse(text, '.', out _).ShouldBeFalse();
    }

    [Fact]
    public void Converts_to_minor_units_with_bankers_rounding_and_currency_digits()
    {
        AmountText.TryParseMinor("12.345", '.', "USD", out var usd).ShouldBeTrue();
        usd.ShouldBe(1234);
        AmountText.TryParseMinor("1,234", '.', "JPY", out var jpy).ShouldBeTrue();
        jpy.ShouldBe(1234);
        AmountText.TryParseMinor("99999999999999999999999", '.', "USD", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(new[] { "-4,50", "3.250,00" }, ',')]
    [InlineData(new[] { "-4.50", "3,250.00" }, '.')]
    [InlineData(new[] { "1,234", "12" }, '.')]
    [InlineData(new[] { "12,5 CR" }, ',')]
    public void Detects_the_decimal_separator_of_a_column(string[] values, char expected)
    {
        AmountText.DetectDecimalSeparator(values).ShouldBe(expected);
    }

    [Theory]
    [InlineData("2026-01-05", "yyyy-MM-dd")]
    [InlineData("2026/1/5", "yyyy-MM-dd")]
    [InlineData("20260105", "yyyyMMdd")]
    [InlineData("01/05/2026", "MM/dd/yyyy")]
    [InlineData("1/5/2026", "MM/dd/yyyy")]
    [InlineData("01-05-2026", "MM/dd/yyyy")]
    [InlineData("01/05/26", "MM/dd/yy")]
    [InlineData("Jan 5, 2026", "MMM d, yyyy")]
    [InlineData("January 5, 2026", "MMM d, yyyy")]
    [InlineData("Jan 5 2026", "MMM d, yyyy")]
    [InlineData("5 Jan 2026", "d MMM yyyy")]
    [InlineData("05-Jan-2026", "d MMM yyyy")]
    [InlineData("2026-01-05T00:00:00", "yyyy-MM-dd")]
    [InlineData("01/05/2026 12:00:00 AM", "MM/dd/yyyy")]
    public void Reads_every_built_in_date_format(string value, string format)
    {
        DateText.TryParse(value, format, out var date).ShouldBeTrue();
        date.ShouldBe(new DateOnly(2026, 1, 5));
    }

    [Fact]
    public void Reads_day_first_and_custom_formats()
    {
        DateText.TryParse("05/01/2026", "dd/MM/yyyy", out var dayFirst).ShouldBeTrue();
        dayFirst.ShouldBe(new DateOnly(2026, 1, 5));
        DateText.TryParse("05.01.26", "dd/MM/yy", out var shortYear).ShouldBeTrue();
        shortYear.ShouldBe(new DateOnly(2026, 1, 5));
        DateText.TryParse("2026|05|01", "yyyy|dd|MM", out var custom).ShouldBeTrue();
        custom.ShouldBe(new DateOnly(2026, 1, 5));
        DateText.TryParse("13/01/2026", "MM/dd/yyyy", out _).ShouldBeFalse();
        DateText.TryParse("1/5/26", "MM/dd/yyyy", out _).ShouldBeFalse(); // two-digit year is not yyyy
    }

    [Fact]
    public void Detection_reports_ambiguity_when_both_orders_fit_and_differ()
    {
        var detection = DateText.Detect(["01/02/2026", "03/04/2026", "12/11/2026"], preferred: null);

        detection.IsAmbiguous.ShouldBeTrue();
        detection.Candidates.Select(c => c.Name).ShouldBe(["MM/dd/yyyy", "dd/MM/yyyy"]);
    }

    [Fact]
    public void Detection_uses_the_users_answer()
    {
        var detection = DateText.Detect(["01/02/2026", "03/04/2026"], DateOrder.DayFirst);

        detection.IsAmbiguous.ShouldBeFalse();
        detection.Best!.Name.ShouldBe("dd/MM/yyyy");
    }

    [Fact]
    public void Detection_is_not_ambiguous_when_every_date_reads_the_same_either_way()
    {
        var detection = DateText.Detect(["01/01/2026", "02/02/2026"], preferred: null);
        detection.IsAmbiguous.ShouldBeFalse();
        detection.Best!.Name.ShouldBe("MM/dd/yyyy");
    }

    [Theory]
    [InlineData(new[] { "01/13/2026", "02/01/2026" }, "MM/dd/yyyy")]
    [InlineData(new[] { "13/01/2026", "02/01/2026" }, "dd/MM/yyyy")]
    [InlineData(new[] { "2026-01-13" }, "yyyy-MM-dd")]
    [InlineData(new[] { "Feb 3, 2026", "Dec 25, 2025" }, "MMM d, yyyy")]
    public void Detection_scans_all_rows_to_disambiguate(string[] values, string expected)
    {
        var detection = DateText.Detect(values, preferred: null);
        detection.IsAmbiguous.ShouldBeFalse();
        detection.Best!.Name.ShouldBe(expected);
    }

    [Fact]
    public void Detection_finds_nothing_for_inconsistent_values()
    {
        DateText.Detect(["01/13/2026", "13/01/2026"], preferred: null).Best.ShouldBeNull();
        DateText.Detect([], preferred: null).Best.ShouldBeNull();
    }
}
