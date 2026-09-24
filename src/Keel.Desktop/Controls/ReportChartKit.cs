using Avalonia;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Avalonia;
using LiveChartsCore.SkiaSharpView.Painting;

namespace Keel.Desktop.Controls;

/// <summary>
/// Shared LiveCharts setup for report charts (PRD 9.8, 9.11): no animation (motion is optional and
/// charts must render the same in tests), no built-in legend (each view has a text legend or
/// table), recessive hairline grid, muted axis labels, and theme-aware paints from
/// <see cref="ChartPalette"/>.
/// </summary>
public static class ReportChartKit
{
    /// <summary>Axis and tooltip text size.</summary>
    public const float TextSize = 12;

    /// <summary>Applies the shared chart settings to a cartesian chart.</summary>
    public static void Configure(CartesianChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        chart.EasingFunction = null;
        chart.AnimationsSpeed = TimeSpan.Zero;
        chart.LegendPosition = LegendPosition.Hidden;
        chart.TooltipPosition = TooltipPosition.Top;
        chart.ZoomMode = ZoomAndPanMode.None;
        chart.FindingStrategy = FindingStrategy.ExactMatchTakeClosest;
        chart.TooltipBackgroundPaint = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Resolve(chart, "Keel.Chart.TooltipBackground")));
        chart.TooltipTextPaint = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Resolve(chart, "Keel.Chart.TooltipText")));
        chart.TooltipTextSize = TextSize;
    }

    /// <summary>Applies the shared chart settings to a pie chart.</summary>
    public static void Configure(PieChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        chart.EasingFunction = null;
        chart.AnimationsSpeed = TimeSpan.Zero;
        chart.LegendPosition = LegendPosition.Hidden;
        chart.TooltipPosition = TooltipPosition.Center;
        chart.TooltipBackgroundPaint = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Resolve(chart, "Keel.Chart.TooltipBackground")));
        chart.TooltipTextPaint = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Resolve(chart, "Keel.Chart.TooltipText")));
        chart.TooltipTextSize = TextSize;
    }

    /// <summary>A fill in a palette slot.</summary>
    public static SolidColorPaint Fill(StyledElement element, int slot, byte? alpha = null) =>
        new(ChartPalette.ToSk(ChartPalette.Slot(element, slot), alpha));

    /// <summary>A stroke in a palette slot.</summary>
    public static SolidColorPaint Stroke(StyledElement element, int slot, float thickness) =>
        new(ChartPalette.ToSk(ChartPalette.Slot(element, slot))) { StrokeThickness = thickness };

    /// <summary>The 2-px surface-coloured gap drawn between adjacent fills.</summary>
    public static SolidColorPaint Gap(StyledElement element) =>
        new(ChartPalette.ToSk(ChartPalette.Resolve(element, "Keel.Chart.Surface"))) { StrokeThickness = 2 };

    /// <summary>The category (month) axis.</summary>
    public static Axis CategoryAxis(StyledElement element, IList<string> labels) => new()
    {
        Labels = labels,
        LabelsPaint = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Resolve(element, "Keel.Chart.Muted"))),
        TextSize = TextSize,
        SeparatorsPaint = null,
        TicksPaint = null,
        LabelsDensity = 0,
    };

    /// <summary>The value axis with a hairline grid and a stronger zero line.</summary>
    public static Axis ValueAxis(StyledElement element, Func<double, string> labeler) => new()
    {
        Labeler = labeler,
        LabelsPaint = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Resolve(element, "Keel.Chart.Muted"))),
        TextSize = TextSize,
        SeparatorsPaint = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Resolve(element, "Keel.Chart.Grid"))) { StrokeThickness = 1 },
        ZeroPaint = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Resolve(element, "Keel.Chart.Baseline"))) { StrokeThickness = 1 },
        TicksPaint = null,
        MinStep = 1,
    };

    /// <summary>Index of the series a clicked point belongs to, or -1.</summary>
    public static int SeriesIndex(IEnumerable<ISeries>? series, LiveChartsCore.Kernel.ChartPoint? point)
    {
        if (series is null || point?.Context.Series is not { } clicked)
        {
            return -1;
        }

        var index = 0;
        foreach (var s in series)
        {
            if (ReferenceEquals(s, clicked))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    /// <summary>Keeps <see cref="IChartView"/> referenced for callers that only need the namespace.</summary>
    internal static bool IsChart(object? value) => value is IChartView;
}
