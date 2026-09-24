using System.Globalization;
using Keel.Domain;

namespace Keel.Domain.Tests;

public class MoneyTests
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void Adds_and_subtracts_in_minor_units()
    {
        var a = new Money(1_050, "USD");
        var b = new Money(-275, "usd");

        (a + b).ShouldBe(new Money(775, "USD"));
        (a - b).ShouldBe(new Money(1_325, "USD"));
        (-a).Amount.ShouldBe(-1_050);
        (a * 3).Amount.ShouldBe(3_150);
    }

    [Fact]
    public void Normalizes_currency_code()
    {
        new Money(1, " eur ").Currency.ShouldBe("EUR");
        Should.Throw<ArgumentException>(() => new Money(1, "EURO"));
        Should.Throw<ArgumentException>(() => new Money(1, "12$"));
    }

    [Fact]
    public void Refuses_to_mix_currencies()
    {
        var usd = new Money(100, "USD");
        var eur = new Money(100, "EUR");

        Should.Throw<CurrencyMismatchException>(() => usd + eur);
        Should.Throw<CurrencyMismatchException>(() => usd - eur);
        Should.Throw<CurrencyMismatchException>(() => usd < eur);
    }

    [Fact]
    public void Arithmetic_overflow_is_detected()
    {
        var max = new Money(long.MaxValue, "USD");
        Should.Throw<OverflowException>(() => max + new Money(1, "USD"));
        Should.Throw<OverflowException>(() => max * 2);
    }

    [Fact]
    public void Compares_by_amount()
    {
        var small = new Money(-5, "USD");
        var big = new Money(5, "USD");

        (small < big).ShouldBeTrue();
        (big >= small).ShouldBeTrue();
        small.IsNegative.ShouldBeTrue();
        big.IsPositive.ShouldBeTrue();
        Money.Zero("USD").IsZero.ShouldBeTrue();
        small.Abs().ShouldBe(big);
    }

    [Theory]
    [InlineData("12.345", 1234)] // midpoint rounds to even (down)
    [InlineData("12.355", 1236)] // midpoint rounds to even (up)
    [InlineData("-12.345", -1234)]
    [InlineData("0.005", 0)]
    [InlineData("0.015", 2)]
    [InlineData("12.3449", 1234)]
    public void FromDecimal_uses_bankers_rounding(string input, long expected)
    {
        Money.FromDecimal(decimal.Parse(input, CultureInfo.InvariantCulture), "USD").Amount.ShouldBe(expected);
    }

    [Theory]
    [InlineData("JPY", "1234", 1234)]
    [InlineData("KWD", "1.2345", 1234)]
    [InlineData("USD", "1.5", 150)]
    public void FromDecimal_respects_currency_minor_digits(string currency, string input, long expected)
    {
        Money.FromDecimal(decimal.Parse(input, CultureInfo.InvariantCulture), currency).Amount.ShouldBe(expected);
    }

    [Fact]
    public void ToDecimal_is_exact()
    {
        new Money(123_456, "USD").ToDecimal().ShouldBe(1234.56m);
        new Money(-1, "USD").ToDecimal().ShouldBe(-0.01m);
        new Money(5, "JPY").ToDecimal().ShouldBe(5m);
    }

    [Fact]
    public void SplitByPercentages_sums_exactly_with_remainder_on_last_part()
    {
        var parts = new Money(1_000, "USD").SplitByPercentages([33.333m, 33.333m, 33.334m]);

        parts.Select(p => p.Amount).ShouldBe([333, 333, 334]);
        parts.Sum(p => p.Amount).ShouldBe(1_000);
    }

    [Fact]
    public void SplitByPercentages_uses_bankers_rounding_per_part()
    {
        // 50% of 5 cents = 2.5 -> 2 (to even); remainder 3 on the last part.
        var parts = new Money(5, "USD").SplitByPercentages([50m, 50m]);
        parts.Select(p => p.Amount).ShouldBe([2, 3]);

        // 50% of 7 cents = 3.5 -> 4 (to even); remainder 3.
        new Money(7, "USD").SplitByPercentages([50m, 50m]).Select(p => p.Amount).ShouldBe([4, 3]);
    }

    [Fact]
    public void SplitByPercentages_handles_negative_amounts()
    {
        var parts = new Money(-10_001, "USD").SplitByPercentages([25m, 25m, 50m]);
        parts.Sum(p => p.Amount).ShouldBe(-10_001);
        parts[0].Amount.ShouldBe(-2_500);
    }

    [Fact]
    public void Allocate_distributes_by_weight_and_sums_exactly()
    {
        var parts = new Money(100, "USD").Allocate([1, 1, 1]);
        parts.Select(p => p.Amount).ShouldBe([33, 33, 34]);

        var weighted = new Money(-1_000, "USD").Allocate([450, 50]);
        weighted.Select(p => p.Amount).ShouldBe([-900, -100]);

        Should.Throw<ArgumentException>(() => new Money(1, "USD").Allocate([0, 0]));
        Should.Throw<ArgumentException>(() => new Money(1, "USD").Allocate([-1, 2]));
    }

    [Theory]
    [InlineData(123_456, "USD", "$1,234.56")]
    [InlineData(-1_200, "USD", "-$12.00")]
    [InlineData(1_234, "JPY", "¥1,234")]
    [InlineData(1_234, "EUR", "€12.34")]
    [InlineData(1_234, "SEK", "SEK12.34")]
    public void Formats_for_display(long amount, string currency, string expected)
    {
        new Money(amount, currency).Format(EnUs).ShouldBe(expected);
    }

    [Fact]
    public void Formats_plain_numbers_and_invariant_strings()
    {
        new Money(123_456, "USD").FormatNumber(EnUs).ShouldBe("1,234.56");
        new Money(-5, "USD").ToString().ShouldBe("-0.05 USD");
        new Money(1_000, "KWD").ToString().ShouldBe("1.000 KWD");
    }

    [Theory]
    [InlineData("12.50", 1_250)]
    [InlineData("$12", 1_200)]
    [InlineData("-1,234.5", -123_450)]
    [InlineData("(12.00)", -1_200)]
    [InlineData("12.345", 1_234)]
    [InlineData(" 7 ", 700)]
    [InlineData("5-", -500)]
    [InlineData("USD 3.10", 310)]
    public void Parses_user_input(string text, long expected)
    {
        Money.TryParse(text, "USD", EnUs, out var money).ShouldBeTrue();
        money.ShouldBe(new Money(expected, "USD"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12..5")]
    [InlineData(null)]
    public void Rejects_invalid_input(string? text)
    {
        Money.TryParse(text, "USD", EnUs, out _).ShouldBeFalse();
        Should.Throw<FormatException>(() => Money.Parse(text ?? string.Empty, "USD", EnUs));
    }

    [Fact]
    public void Parses_with_culture_decimal_separator()
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        Money.Parse("1.234,56", "EUR", de).Amount.ShouldBe(123_456);
    }

    [Fact]
    public void Round_trips_format_and_parse()
    {
        var original = new Money(-987_654, "USD");
        Money.Parse(original.Format(EnUs), "USD", EnUs).ShouldBe(original);
    }

    [Fact]
    public void Sum_adds_a_sequence()
    {
        Money.Sum([new Money(1, "USD"), new Money(2, "USD"), new Money(-4, "USD")], "USD").Amount.ShouldBe(-1);
        Money.Sum([], "USD").ShouldBe(Money.Zero("USD"));
    }
}
