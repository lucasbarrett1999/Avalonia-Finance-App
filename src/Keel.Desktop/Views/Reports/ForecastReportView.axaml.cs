using Avalonia.Controls;
using Keel.Desktop.Controls;
using Keel.Desktop.ViewModels.Reports;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;

namespace Keel.Desktop.Views.Reports;

/// <summary>
/// View for <see cref="ForecastReportViewModel"/>: one line per account and a thicker combined line, the
/// lowest combined day shaded with a marker, and the floor as a dashed line. A click on a point explains
/// that day of that line.
/// </summary>
public partial class ForecastReportView : UserControl
{
    private ForecastReportViewModel? _vm;
    private List<ISeries> _lines = [];
    private List<ForecastSeriesItem> _ordered = [];

    /// <summary>Creates the view.</summary>
    public ForecastReportView()
    {
        InitializeComponent();
        ForecastChart.DataPointerDown += (chart, points) => OnPointDown(chart, points.FirstOrDefault());
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.ChartChanged -= OnChartChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as ForecastReportViewModel;
        if (_vm is not null)
        {
            _vm.ChartChanged += OnChartChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }

        Rebuild();
    }

    private void OnChartChanged(object? sender, EventArgs e) => Rebuild();

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ForecastReportViewModel.UseFloor))
        {
            Rebuild();
        }
    }

    private void OnPointDown(IChartView chart, ChartPoint? point)
    {
        var index = ReportChartKit.SeriesIndex(_lines, point);
        if (index >= 0 && index < _ordered.Count && point is not null && _vm is not null && point.Index < _vm.Dates.Count)
        {
            _vm.Explain(_vm.Dates[point.Index], _ordered[index].AccountId);
        }
    }

    private void Rebuild()
    {
        ReportChartKit.Configure(ForecastChart);
        var series = _vm?.Series ?? [];
        var dates = _vm?.Dates ?? [];
        var currency = _vm?.Currency ?? Keel.Domain.Currency.Default;
        var labels = dates.Select((d, i) => i % 7 == 0 ? d.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture) : string.Empty).ToList();
        ForecastChart.XAxes = [ReportChartKit.CategoryAxis(this, labels)];
        ForecastChart.YAxes = [ReportChartKit.ValueAxis(this, v => ReportFormat.Axis(v, currency))];

        // Accounts first (palette slots), the combined line last so it draws on top; clicks map back by item.
        var ordered = series.Where(s => !s.IsCombined).Concat(series.Where(s => s.IsCombined)).ToList();
        _ordered = ordered;
        _lines = [];
        foreach (var item in ordered)
        {
            _lines.Add(new LineSeries<double>
            {
                Name = item.Name,
                Values = item.Balances.Select(b => ReportFormat.Major(b, item.Currency)).ToList(),
                Stroke = ReportChartKit.Stroke(this, item.Slot, item.IsCombined ? 3f : 1.5f),
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                YToolTipLabelFormatter = p => Services.LedgerText.Money(item.Balances[p.Index], item.Currency) + " · " + dates[p.Index].ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture),
            });
        }

        var all = new List<ISeries>(_lines);
        var sections = new List<RectangularSection>();
        if (series.FirstOrDefault(s => s.IsCombined) is { } combined && dates.Count > 0)
        {
            var low = dates.ToList().IndexOf(combined.LowestDate);
            if (low >= 0)
            {
                sections.Add(new RectangularSection
                {
                    Xi = low - 0.5,
                    Xj = low + 0.5,
                    Fill = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Slot(this, 3), 60)),
                });
                all.Add(new ScatterSeries<LiveChartsCore.Defaults.ObservablePoint>
                {
                    Name = Keel.Desktop.Resources.Strings.Forecast_LowestLegend,
                    Values = [new LiveChartsCore.Defaults.ObservablePoint(low, ReportFormat.Major(combined.Lowest, combined.Currency))],
                    GeometrySize = 12,
                    Fill = ReportChartKit.Fill(this, ChartPalette.InkSlot),
                    Stroke = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Resolve(this, "Keel.Chart.Surface"))) { StrokeThickness = 2 },
                    YToolTipLabelFormatter = _ => combined.LowestText,
                });
            }
        }

        if (_vm is { UseFloor: true } vm)
        {
            var floor = ReportFormat.Major(vm.FloorAmount, currency);
            sections.Add(new RectangularSection
            {
                Yi = floor,
                Yj = floor,
                Stroke = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Slot(this, 7))) { StrokeThickness = 1.5f, PathEffect = new DashEffect([6, 4]) },
            });
        }

        ForecastChart.Sections = sections;
        ForecastChart.Series = all;
    }
}
