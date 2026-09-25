using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Keel.Desktop.Tests;

/// <summary>
/// Accessibility checks over a live visual tree (PRD 8, 9.11, 11): every interactive control a user can reach
/// has a screen-reader name, and every visible text meets WCAG AA contrast against what is behind it.
/// </summary>
internal static class AccessibilityAudit
{
    /// <summary>Interactive controls (not parts of another control's template) whose automation name is empty.</summary>
    public static IEnumerable<string> UnnamedControls(Visual root, string screen)
    {
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            if (!IsInteractive(control) || control.TemplatedParent is not null || !control.IsEffectivelyVisible || control.Bounds.Width <= 0)
            {
                continue;
            }

            var peer = ControlAutomationPeer.CreatePeerForElement(control);
            if (string.IsNullOrWhiteSpace(peer.GetName()))
            {
                yield return $"{screen}: {control.GetType().Name} '{control.Name}' in {Path(control)} has no automation name";
            }
        }
    }

    /// <summary>Visible text whose contrast with its background is below WCAG AA (4.5:1, or 3:1 for large text).</summary>
    public static IEnumerable<string> LowContrastText(Visual root, string screen)
    {
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!text.IsEffectivelyVisible || string.IsNullOrWhiteSpace(text.Text) || !text.IsEffectivelyEnabled || text.Bounds.Width <= 0)
            {
                continue;
            }

            if (text.Foreground is not ISolidColorBrush foreground || Background(text) is not { } background)
            {
                continue;
            }

            var fg = Blend(foreground.Color, foreground.Opacity * text.Opacity, background);
            var ratio = Contrast(fg, background);
            var large = text.FontSize >= 24 || (text.FontSize >= 18.66 && text.FontWeight >= FontWeight.Bold);
            var needed = large ? 3.0 : 4.5;
            if (ratio < needed - 0.005)
            {
                yield return $"{screen}: \"{Trim(text.Text)}\" {fg} on {background} = {ratio:0.00} (< {needed}) in {Path(text)}";
            }
        }
    }

    /// <summary>Visible amounts (money text) drawn without tabular figures (PRD 8, 9.11).</summary>
    public static IEnumerable<string> AmountsWithoutTabularFigures(Visual root, string screen)
    {
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!text.IsEffectivelyVisible || text.Text is not { } value || !MoneyText.IsMatch(value.Trim()))
            {
                continue;
            }

            if (text.FontFeatures is not { } features || !features.Any(f => f.Tag == "tnum" && f.Value != 0))
            {
                yield return $"{screen}: amount \"{value}\" without tabular figures in {Path(text)}";
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex MoneyText = new(@"^[−-]?\(?[−-]?[^\d\s(]{0,3}\s?\d{1,3}([,.\u00A0\u202F ]\d{3})*[.,]\d{2}\)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>WCAG relative luminance contrast ratio of two opaque colours.</summary>
    public static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>WCAG relative luminance.</summary>
    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    /// <summary><paramref name="top"/> with <paramref name="opacity"/> over an opaque <paramref name="under"/>.</summary>
    public static Color Blend(Color top, double opacity, Color under)
    {
        var a = top.A / 255.0 * opacity;
        byte Mix(byte t, byte u) => (byte)Math.Round((t * a) + (u * (1 - a)));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    private static bool IsInteractive(Control control) => control is Button or ToggleButton or TextBox or ComboBox or ListBox or Slider
        or NumericUpDown or MenuItem or AutoCompleteBox or CalendarDatePicker or DatePicker or DataGrid or TabItem or Expander or ToggleSwitch;

    // The first opaque-ish background behind the text, with translucent layers composed over it.
    private static Color? Background(Visual visual)
    {
        var layers = new List<(Color Color, double Opacity)>();
        foreach (var ancestor in visual.GetVisualAncestors())
        {
            IBrush? brush = ancestor switch
            {
                Border b => b.Background,
                Panel p => p.Background,
                ContentPresenter c => c.Background,
                TemplatedControl t when t.Template is null => t.Background,
                TopLevel top => top.Background,
                _ => null,
            };
            if (brush is null)
            {
                continue;
            }

            if (brush is not ISolidColorBrush solid)
            {
                return null; // gradients, images, charts: not checked here
            }

            var opacity = solid.Opacity * ((Visual)ancestor).Opacity;
            if (solid.Color.A == 0 || opacity == 0)
            {
                continue;
            }

            layers.Add((solid.Color, opacity));
            if (solid.Color.A == 255 && opacity >= 1)
            {
                var color = solid.Color;
                for (var i = layers.Count - 2; i >= 0; i--)
                {
                    color = Blend(layers[i].Color, layers[i].Opacity, color);
                }

                return color;
            }
        }

        return null;
    }

    private static string Trim(string text) => text.Length <= 40 ? text : text[..40] + "…";

    private static string Path(Visual visual) => string.Join(" < ", visual.GetVisualAncestors().OfType<Control>().Where(c => !string.IsNullOrEmpty(c.Name) || c is UserControl).Take(3).Select(c => string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.GetType().Name + "#" + c.Name));
}
