using System.Globalization;
using Keel.Domain.Ledger;

namespace Keel.Domain.Tests.Ledger;

public class MoneyExpressionTests
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    [Theory]
    [InlineData("12.50+3", 15.50)]
    [InlineData("12.50 + 3", 15.50)]
    [InlineData("3*4.99", 14.97)]
    [InlineData("100/4", 25)]
    [InlineData("(20+5)*2", 50)]
    [InlineData("10-2.5-2.5", 5)]
    [InlineData("-5+2", -3)]
    [InlineData("$1,234.56", 1234.56)]
    [InlineData("2*-3", -6)]
    public void Evaluates_us_input(string text, double expected)
    {
        MoneyExpression.TryEvaluate(text, Us, out var value).ShouldBeTrue();
        value.ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("12,50+3", 15.50)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("10,5*2", 21)]
    public void Evaluates_locale_input(string text, double expected)
    {
        MoneyExpression.TryEvaluate(text, German, out var value).ShouldBeTrue();
        value.ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("5/0")]
    [InlineData("3+")]
    [InlineData("(3+4")]
    [InlineData("1.2.3")]
    public void Rejects_invalid_input(string text)
    {
        MoneyExpression.TryEvaluate(text, Us, out _).ShouldBeFalse();
    }

    [Fact]
    public void Rounds_to_minor_units_with_bankers_rounding()
    {
        MoneyExpression.TryEvaluate("100/3", Us, out var value).ShouldBeTrue();
        Money.FromDecimal(value, "USD").Amount.ShouldBe(3_333);
        MoneyExpression.IsArithmetic("12+3").ShouldBeTrue();
        MoneyExpression.IsArithmetic("-12").ShouldBeFalse();
    }
}
