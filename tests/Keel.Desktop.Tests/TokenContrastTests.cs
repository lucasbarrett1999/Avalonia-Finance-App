using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Keel.Application.Settings;
using Keel.Desktop.Services;

namespace Keel.Desktop.Tests;

/// <summary>WCAG AA contrast of the design-token palette in both themes and every accent (PRD 9.11).</summary>
public sealed class TokenContrastTests
{
    private static readonly string[] Surfaces =
    [
        "Keel.WindowBackground", "Keel.SurfaceBackground", "Keel.SidebarBackground", "Keel.RowAlternate", "Keel.EditorBackground",
        "Keel.NavHover", "Keel.NavSelected", "Keel.WarningBackground", "Keel.Budget.GroupBackground", "Keel.Budget.SelectedRow",
        "Keel.Bills.DayBackground", "Keel.Bills.OtherMonthBackground", "Keel.Bills.ChipBackground", "Keel.Bills.ChipPaidBackground",
        "Keel.Bills.ChipIncomeBackground", "Keel.Bills.PillBackground", "Keel.Bills.DetectedBackground", "Keel.Bills.GhostBackground",
        "Keel.Chart.RowHover",
    ];

    public static TheoryData<string, string, string, double> Pairs()
    {
        var data = new TheoryData<string, string, string, double>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            // Body text on every surface (4.5:1).
            foreach (var text in new[] { "Keel.TextPrimary", "Keel.TextSecondary" })
            {
                foreach (var surface in Surfaces)
                {
                    data.Add(theme, text, surface, 4.5);
                }
            }

            // Semantic text on the page, card, sidebar and alternate rows.
            foreach (var text in new[] { "Keel.Negative", "Keel.Positive", "Keel.Warning", "Keel.Accent" })
            {
                foreach (var surface in new[] { "Keel.WindowBackground", "Keel.SurfaceBackground", "Keel.SidebarBackground", "Keel.RowAlternate" })
                {
                    data.Add(theme, text, surface, 4.5);
                }
            }

            // Foreground tokens on their own backgrounds.
            data.Add(theme, "Keel.NavSelectedForeground", "Keel.NavSelected", 4.5);
            data.Add(theme, "Keel.BadgeForeground", "Keel.BadgeBackground", 4.5);
            data.Add(theme, "Keel.Warning", "Keel.WarningBackground", 4.5);
            data.Add(theme, "Keel.IconCircleForeground", "Keel.IconCircle", 4.5);
            foreach (var pill in new[] { "Positive", "Zero", "Credit", "Cash", "RtaPositive", "RtaNegative", "Banner" })
            {
                data.Add(theme, $"Keel.Budget.{pill}Foreground", $"Keel.Budget.{pill}Background", 4.5);
            }

            // Non-text graphics that carry meaning (WCAG 1.4.11, 3:1).
            foreach (var graphic in new[] { "Keel.Budget.Cursor", "Keel.Bills.TodayBorder", "Keel.Chart.RingFill", "Keel.Chart.RingDone", "Keel.Budget.BarPositive", "Keel.Budget.BarNegative", "Keel.Bills.UnreadDot" })
            {
                data.Add(theme, graphic, "Keel.SurfaceBackground", 3.0);
            }
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Pairs))]
    public void Token_pairs_meet_WCAG_AA(string theme, string foreground, string background, double minimum)
    {
        var variant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var ratio = AccessibilityAudit.Contrast(Color(foreground, variant), Color(background, variant));
        ratio.ShouldBeGreaterThanOrEqualTo(minimum, $"{foreground} on {background} ({theme}) is {ratio:0.00}:1");
    }

    [AvaloniaFact]
    public void Every_accent_reads_on_the_page_and_under_badge_and_button_text()
    {
        foreach (var (accent, (light, dark)) in AppearanceService.Accents)
        {
            foreach (var (variant, color) in new[] { (ThemeVariant.Light, light), (ThemeVariant.Dark, dark) })
            {
                foreach (var surface in new[] { "Keel.WindowBackground", "Keel.SurfaceBackground" })
                {
                    AccessibilityAudit.Contrast(color, Color(surface, variant)).ShouldBeGreaterThanOrEqualTo(4.5, $"{accent} accent on {surface} ({variant})");
                }

                AccessibilityAudit.Contrast(Color("Keel.BadgeForeground", variant), color).ShouldBeGreaterThanOrEqualTo(4.5, $"badge text on {accent} ({variant})");
                AccessibilityAudit.Contrast(Color("AccentButtonForeground", variant), color).ShouldBeGreaterThanOrEqualTo(4.5, $"button text on {accent} ({variant})");
            }
        }

        Enum.GetValues<AppAccent>().ShouldAllBe(a => AppearanceService.Accents.ContainsKey(a));
    }

    [Fact]
    public void Contrast_matches_the_WCAG_reference_values()
    {
        AccessibilityAudit.Contrast(Colors.Black, Colors.White).ShouldBe(21, 0.01);
        AccessibilityAudit.Contrast(Colors.White, Colors.White).ShouldBe(1, 0.001);
        AccessibilityAudit.Contrast(Avalonia.Media.Color.Parse("#767676"), Colors.White).ShouldBe(4.54, 0.01);
    }

    private static Color Color(string key, ThemeVariant variant)
    {
        Avalonia.Application.Current!.TryGetResource(key, variant, out var value).ShouldBeTrue($"resource {key} ({variant})");
        return value switch
        {
            ISolidColorBrush brush => brush.Color,
            Color color => color,
            _ => throw new InvalidOperationException($"{key} is not a colour"),
        };
    }
}
