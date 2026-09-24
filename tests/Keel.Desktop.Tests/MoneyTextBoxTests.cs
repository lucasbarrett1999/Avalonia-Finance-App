using System.Globalization;
using Avalonia.Headless.XUnit;
using Keel.Desktop.Controls;

namespace Keel.Desktop.Tests;

public class MoneyTextBoxTests
{
    [AvaloniaFact]
    public void Evaluates_inline_math_into_minor_units()
    {
        var box = new MoneyTextBox { Culture = CultureInfo.GetCultureInfo("en-US"), Text = "12.50+3" };
        box.Commit().ShouldBeTrue();
        box.Value.ShouldBe(1_550);
        box.Text.ShouldBe("15.50");
        box.HasError.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void Keeps_invalid_text_and_flags_an_error()
    {
        var box = new MoneyTextBox { Culture = CultureInfo.GetCultureInfo("en-US"), Value = 500 };
        box.Text = "12..5";
        box.Commit().ShouldBeFalse();
        box.HasError.ShouldBeTrue();
        box.Value.ShouldBe(500);
        box.Classes.ShouldNotBeNull();
    }

    [AvaloniaFact]
    public void Formats_values_in_the_culture_and_currency()
    {
        var box = new MoneyTextBox { Culture = CultureInfo.GetCultureInfo("de-DE"), Currency = "EUR", Value = 123_456 };
        box.Text.ShouldBe("1.234,56");
        box.Text = "10,5*2";
        box.Commit().ShouldBeTrue();
        box.Value.ShouldBe(2_100);

        var yen = new MoneyTextBox { Culture = CultureInfo.GetCultureInfo("en-US"), Currency = "JPY", Text = "1000/3" };
        yen.Commit().ShouldBeTrue();
        yen.Value.ShouldBe(333);

        var empty = new MoneyTextBox { Value = 0 };
        empty.Text.ShouldBe(string.Empty);
        new MoneyTextBox { ShowZeroAsEmpty = false, Culture = CultureInfo.GetCultureInfo("en-US") }.Text.ShouldBe("0.00");
    }
}
