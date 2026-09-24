using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using SkiaSharp;

namespace Keel.Desktop.Controls;

/// <summary>
/// The categorical chart palette of <c>Styles/Charts.axaml</c> (PRD 9.8): eight slots in a fixed
/// order, then "Other". Colours are looked up for the element's current theme, so charts and
/// swatches re-resolve them when the theme changes.
/// </summary>
public static class ChartPalette
{
    /// <summary>Number of categorical slots before series fold into "Other".</summary>
    public const int SlotCount = 8;

    /// <summary>The slot value that means "Other" (neutral grey).</summary>
    public const int OtherSlot = -1;

    /// <summary>The slot value that means the primary ink (a total drawn over coloured series).</summary>
    public const int InkSlot = -2;

    /// <summary>Resource key of a slot (0-based), of the ink, or of "Other".</summary>
    public static string Key(int slot) => slot switch
    {
        >= 0 and < SlotCount => "Keel.Chart.Series" + (slot + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
        InkSlot => "Keel.Chart.Ink",
        _ => "Keel.Chart.Other",
    };

    /// <summary>Resolves a chart colour resource for <paramref name="element"/>'s theme.</summary>
    public static Color Resolve(StyledElement element, string key)
    {
        ArgumentNullException.ThrowIfNull(element);
        var theme = element.ActualThemeVariant ?? ThemeVariant.Light;
        if (element.TryFindResource(key, theme, out var value) && value is Color color)
        {
            return color;
        }

        return Avalonia.Application.Current is { } app && app.TryGetResource(key, theme, out var appValue) && appValue is Color appColor
            ? appColor
            : Colors.Gray;
    }

    /// <summary>The colour of a slot for <paramref name="element"/>'s theme.</summary>
    public static Color Slot(StyledElement element, int slot) => Resolve(element, Key(slot));

    /// <summary>Converts to a Skia colour (for LiveCharts paints).</summary>
    public static SKColor ToSk(Color color, byte? alpha = null) => new(color.R, color.G, color.B, alpha ?? color.A);
}

/// <summary>How a legend swatch is drawn.</summary>
public enum ChartSwatchKind
{
    /// <summary>A filled rounded square (bars, slices, areas).</summary>
    Box,

    /// <summary>A short line with a round marker (line series).</summary>
    Line,
}

/// <summary>A legend swatch in a chart palette slot; follows the theme.</summary>
public class ChartSwatch : Control
{
    /// <summary>Defines <see cref="Slot"/>.</summary>
    public static readonly StyledProperty<int> SlotProperty =
        AvaloniaProperty.Register<ChartSwatch, int>(nameof(Slot));

    /// <summary>Defines <see cref="Kind"/>.</summary>
    public static readonly StyledProperty<ChartSwatchKind> KindProperty =
        AvaloniaProperty.Register<ChartSwatch, ChartSwatchKind>(nameof(Kind));

    static ChartSwatch()
    {
        AffectsRender<ChartSwatch>(SlotProperty, KindProperty);
        WidthProperty.OverrideDefaultValue<ChartSwatch>(12);
        HeightProperty.OverrideDefaultValue<ChartSwatch>(12);
    }

    /// <summary>Creates the swatch and redraws it on theme changes.</summary>
    public ChartSwatch()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Palette slot (0-based); <see cref="ChartPalette.OtherSlot"/> for "Other".</summary>
    public int Slot
    {
        get => GetValue(SlotProperty);
        set => SetValue(SlotProperty, value);
    }

    /// <summary>Box or line.</summary>
    public ChartSwatchKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var brush = new SolidColorBrush(ChartPalette.Slot(this, Slot));
        var bounds = new Rect(Bounds.Size);
        if (Kind == ChartSwatchKind.Line)
        {
            var y = bounds.Height / 2;
            context.DrawLine(new Pen(brush, 2), new Point(0, y), new Point(bounds.Width, y));
            context.DrawEllipse(brush, null, new Point(bounds.Width / 2, y), 3.5, 3.5);
        }
        else
        {
            context.DrawRectangle(brush, null, bounds, 3, 3);
        }
    }
}
