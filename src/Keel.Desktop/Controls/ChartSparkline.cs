using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Keel.Desktop.Controls;

/// <summary>
/// A compact trend line (the dashboard's net-worth sparkline, PRD 9.2): the values scaled to the
/// control, a hairline zero baseline when the range crosses zero, and a marker on the last value.
/// The card shows the numbers as text; the line only shows the shape.
/// </summary>
public class ChartSparkline : Control
{
    /// <summary>Defines <see cref="Values"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<ChartSparkline, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>Defines <see cref="Slot"/>.</summary>
    public static readonly StyledProperty<int> SlotProperty =
        AvaloniaProperty.Register<ChartSparkline, int>(nameof(Slot));

    static ChartSparkline()
    {
        AffectsRender<ChartSparkline>(ValuesProperty, SlotProperty);
    }

    /// <summary>Creates the sparkline and redraws it on theme changes.</summary>
    public ChartSparkline()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Values in order.</summary>
    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Palette slot of the line.</summary>
    public int Slot
    {
        get => GetValue(SlotProperty);
        set => SetValue(SlotProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Values is not { Count: > 1 } values || Bounds.Width < 8 || Bounds.Height < 8)
        {
            return;
        }

        const double pad = 4;
        var min = values.Min();
        var max = values.Max();
        var span = max - min;
        if (span <= 0)
        {
            span = Math.Abs(max) > 0 ? Math.Abs(max) : 1;
            min -= span / 2;
        }

        var width = Bounds.Width - (2 * pad);
        var height = Bounds.Height - (2 * pad);
        Point At(int i) => new(pad + (width * i / (values.Count - 1)), pad + (height * (1 - ((values[i] - min) / span))));

        if (min < 0 && max > 0)
        {
            var zero = pad + (height * (1 - ((0 - min) / span)));
            context.DrawLine(new Pen(new SolidColorBrush(ChartPalette.Resolve(this, "Keel.Chart.Baseline")), 1), new Point(pad, zero), new Point(pad + width, zero));
        }

        var brush = new SolidColorBrush(ChartPalette.Slot(this, Slot));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(0), isFilled: false);
            for (var i = 1; i < values.Count; i++)
            {
                ctx.LineTo(At(i));
            }

            ctx.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(brush, 2, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), geometry);
        context.DrawEllipse(brush, new Pen(new SolidColorBrush(ChartPalette.Resolve(this, "Keel.Chart.Surface")), 2), At(values.Count - 1), 4, 4);
    }
}
