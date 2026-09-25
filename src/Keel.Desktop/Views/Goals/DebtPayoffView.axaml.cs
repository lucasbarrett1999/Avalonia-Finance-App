using Avalonia.Controls;
using Keel.Desktop.Controls;
using Keel.Desktop.ViewModels.Goals;
using Keel.Desktop.ViewModels.Reports;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;

namespace Keel.Desktop.Views.Goals;

/// <summary>
/// View for <see cref="DebtPayoffViewModel"/>: each debt's balance as a stacked area under the plan and the
/// minimum-payments-only total as a dashed line. At most 30 years are drawn.
/// </summary>
public partial class DebtPayoffView : UserControl
{
    /// <summary>Longest horizon drawn, in months.</summary>
    public const int MaxChartMonths = 360;

    private DebtPayoffViewModel? _vm;

    /// <summary>Creates the view.</summary>
    public DebtPayoffView()
    {
        InitializeComponent();
        DebtChart.DataPointerDown += (_, points) => OnPointDown(points.FirstOrDefault());
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.ChartChanged -= OnChartChanged;
        }

        _vm = DataContext as DebtPayoffViewModel;
        if (_vm is not null)
        {
            _vm.ChartChanged += OnChartChanged;
        }

        Rebuild();
    }

    private void OnChartChanged(object? sender, EventArgs e) => Rebuild();

    private void OnPointDown(ChartPoint? point)
    {
        var index = ReportChartKit.SeriesIndex(DebtChart.Series, point);
        if (_vm is not null && index >= 0 && index < _vm.Rows.Count)
        {
            _vm.OpenBand(index);
        }
    }

    private void Rebuild()
    {
        ReportChartKit.Configure(DebtChart);
        var rows = _vm?.Rows ?? [];
        var minimum = _vm?.MinimumOnlyTotals ?? [];
        var currency = _vm?.Currency ?? Keel.Domain.Currency.Default;
        var start = _vm?.StartMonth ?? default;
        var length = Math.Min(MaxChartMonths + 1, Math.Max(rows.Select(r => r.Balances.Count).DefaultIfEmpty(0).Max(), minimum.Count));

        // Month axis: a label every few months so long plans stay readable.
        var step = Math.Max(1, (int)Math.Ceiling(length / 12.0));
        DebtChart.XAxes =
        [
            new Axis
            {
                Labeler = v => v >= 0 && v < length ? ReportFormat.ShortMonth(start.AddMonths((int)Math.Round(v))) : string.Empty,
                MinStep = step,
                ForceStepToMin = true,
                LabelsPaint = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Resolve(this, "Keel.Chart.Muted"))),
                TextSize = ReportChartKit.TextSize,
                SeparatorsPaint = null,
                TicksPaint = null,
                MinLimit = 0,
                MaxLimit = Math.Max(1, length - 1),
            },
        ];
        var y = ReportChartKit.ValueAxis(this, v => ReportFormat.Axis(v, currency));
        y.MinLimit = 0;
        DebtChart.YAxes = [y];

        var series = new List<ISeries>();
        foreach (var row in rows)
        {
            var values = Enumerable.Range(0, length).Select(i => ReportFormat.Major(i < row.Balances.Count ? row.Balances[i] : 0, currency)).ToList();
            series.Add(new StackedAreaSeries<double>
            {
                Name = row.Name,
                Values = values,
                Fill = ReportChartKit.Fill(this, row.Slot, 170),
                Stroke = ReportChartKit.Stroke(this, row.Slot, 1),
                GeometrySize = 0,
                LineSmoothness = 0,
                YToolTipLabelFormatter = p => ReportFormat.Money(p.Index < row.Balances.Count ? row.Balances[p.Index] : 0, currency),
                XToolTipLabelFormatter = p => ReportFormat.MonthLabel(start.AddMonths(p.Index)),
            });
        }

        if (minimum.Count > 0)
        {
            series.Add(new LineSeries<double>
            {
                Name = Keel.Desktop.Resources.Strings.Debt_MinimumLine,
                Values = Enumerable.Range(0, length).Select(i => ReportFormat.Major(i < minimum.Count ? minimum[i] : 0, currency)).ToList(),
                Stroke = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Slot(this, ChartPalette.InkSlot))) { StrokeThickness = 2, PathEffect = new DashEffect([6, 4]) },
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                YToolTipLabelFormatter = p => ReportFormat.Money(p.Index < minimum.Count ? minimum[p.Index] : 0, currency),
            });
        }

        DebtChart.Series = series;
    }
}
