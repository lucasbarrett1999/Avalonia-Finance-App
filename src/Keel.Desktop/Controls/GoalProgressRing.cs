using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Keel.Desktop.Controls;

/// <summary>
/// A goal's progress ring (F-GOAL-1, PRD 9.7): a track circle and an arc for
/// <see cref="Progress"/> (0 to 1). A completed goal draws in the "done" colour; the numbers are
/// always shown as text next to the ring, so colour is not the only signal.
/// </summary>
public class GoalProgressRing : Control
{
    /// <summary>Defines <see cref="Progress"/>.</summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<GoalProgressRing, double>(nameof(Progress));

    /// <summary>Defines <see cref="Thickness"/>.</summary>
    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<GoalProgressRing, double>(nameof(Thickness), 8);

    static GoalProgressRing()
    {
        AffectsRender<GoalProgressRing>(ProgressProperty, ThicknessProperty);
    }

    /// <summary>Creates the ring and redraws it on theme changes.</summary>
    public GoalProgressRing()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Fraction complete; clamped to 0..1 when drawn.</summary>
    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>Stroke thickness.</summary>
    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= Thickness * 2)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (size - Thickness) / 2;
        var progress = double.IsNaN(Progress) ? 0 : Math.Clamp(Progress, 0, 1);
        context.DrawEllipse(null, new Pen(Brush("Keel.Chart.RingTrack"), Thickness), center, radius, radius);
        if (progress <= 0)
        {
            return;
        }

        var pen = new Pen(Brush(progress >= 1 ? "Keel.Chart.RingDone" : "Keel.Chart.RingFill"), Thickness, lineCap: PenLineCap.Round);
        if (progress >= 1)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = progress * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + (radius * Math.Sin(angle)), center.Y - (radius * Math.Cos(angle)));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false);
            ctx.ArcTo(end, new Size(radius, radius), 0, isLargeArc: progress > 0.5, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;
}
