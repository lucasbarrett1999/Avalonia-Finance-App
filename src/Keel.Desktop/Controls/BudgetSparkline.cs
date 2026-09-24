using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Keel.Desktop.Controls;

/// <summary>
/// A small bar sparkline of monthly amounts (the budget inspector's six-month history of
/// Available). Bars grow up from a baseline for positive values and down for negative ones, so the
/// sign is shown by direction as well as colour.
/// </summary>
public class BudgetSparkline : Control
{
    /// <summary>Defines <see cref="Values"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<long>?> ValuesProperty =
        AvaloniaProperty.Register<BudgetSparkline, IReadOnlyList<long>?>(nameof(Values));

    /// <summary>Defines <see cref="PositiveBrush"/>.</summary>
    public static readonly StyledProperty<IBrush?> PositiveBrushProperty =
        AvaloniaProperty.Register<BudgetSparkline, IBrush?>(nameof(PositiveBrush), Brushes.SeaGreen);

    /// <summary>Defines <see cref="NegativeBrush"/>.</summary>
    public static readonly StyledProperty<IBrush?> NegativeBrushProperty =
        AvaloniaProperty.Register<BudgetSparkline, IBrush?>(nameof(NegativeBrush), Brushes.IndianRed);

    /// <summary>Defines <see cref="BaselineBrush"/>.</summary>
    public static readonly StyledProperty<IBrush?> BaselineBrushProperty =
        AvaloniaProperty.Register<BudgetSparkline, IBrush?>(nameof(BaselineBrush), Brushes.Gray);

    static BudgetSparkline()
    {
        AffectsRender<BudgetSparkline>(ValuesProperty, PositiveBrushProperty, NegativeBrushProperty, BaselineBrushProperty);
    }

    /// <summary>Amounts in minor units, oldest first.</summary>
    public IReadOnlyList<long>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Fill of bars above the baseline.</summary>
    public IBrush? PositiveBrush
    {
        get => GetValue(PositiveBrushProperty);
        set => SetValue(PositiveBrushProperty, value);
    }

    /// <summary>Fill of bars below the baseline.</summary>
    public IBrush? NegativeBrush
    {
        get => GetValue(NegativeBrushProperty);
        set => SetValue(NegativeBrushProperty, value);
    }

    /// <summary>Colour of the zero line.</summary>
    public IBrush? BaselineBrush
    {
        get => GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        base.Render(context);
        var values = Values;
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (values is null || values.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        var max = Math.Max(0, values.Max());
        var min = Math.Min(0, values.Min());
        var span = (double)(max - min);
        var baseline = span == 0 ? height - 1 : height * max / span;
        var slot = width / values.Count;
        var barWidth = Math.Max(2, slot * 0.6);
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            var x = (i * slot) + ((slot - barWidth) / 2);
            var length = span == 0 ? 0 : Math.Abs(value) / span * height;
            length = value == 0 ? 0 : Math.Max(2, length);
            var rect = value >= 0
                ? new Rect(x, baseline - length, barWidth, length)
                : new Rect(x, baseline, barWidth, length);
            if (length > 0)
            {
                context.FillRectangle(value >= 0 ? PositiveBrush ?? Brushes.Green : NegativeBrush ?? Brushes.Red, rect, 2);
            }
        }

        context.DrawLine(new Pen(BaselineBrush ?? Brushes.Gray, 1), new Point(0, Math.Round(baseline) + 0.5), new Point(width, Math.Round(baseline) + 0.5));
    }
}
