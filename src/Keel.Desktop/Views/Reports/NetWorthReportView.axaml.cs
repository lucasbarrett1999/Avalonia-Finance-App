using Avalonia.Controls;
using Keel.Desktop.Controls;
using Keel.Desktop.ViewModels.Reports;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

namespace Keel.Desktop.Views.Reports;

/// <summary>View for <see cref="NetWorthReportViewModel"/>: the net worth line, optionally over stacked account areas.</summary>
public partial class NetWorthReportView : UserControl
{
    private NetWorthReportViewModel? _vm;
    private List<NetWorthSeriesItem> _bands = [];

    /// <summary>Creates the view.</summary>
    public NetWorthReportView()
    {
        InitializeComponent();
        NetWorthChart.DataPointerDown += (chart, points) => OnPointDown(chart, points.FirstOrDefault());
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

        _vm = DataContext as NetWorthReportViewModel;
        if (_vm is not null)
        {
            _vm.ChartChanged += OnChartChanged;
        }

        Rebuild();
    }

    private void OnChartChanged(object? sender, EventArgs e) => Rebuild();

    private void OnPointDown(IChartView chart, ChartPoint? point)
    {
        var index = ReportChartKit.SeriesIndex(NetWorthChart.Series, point);
        if (index < 0 || point is null || _vm is null)
        {
            return;
        }

        // Bands come first (assets, then liabilities); the net line is last.
        var band = index < _bands.Count ? _vm.Series.ToList().IndexOf(_bands[index]) : -1;
        _vm.OpenPoint(band, point.Index);
    }

    private void Rebuild()
    {
        ReportChartKit.Configure(NetWorthChart);
        var rows = _vm?.Rows ?? [];
        var currency = _vm?.Report?.Currency ?? Keel.Domain.Currency.Default;
        NetWorthChart.XAxes = [ReportChartKit.CategoryAxis(this, rows.Select(r => ReportFormat.ShortMonth(r.Date)).ToList())];
        NetWorthChart.YAxes = [ReportChartKit.ValueAxis(this, v => ReportFormat.Axis(v, currency))];

        var series = new List<ISeries>();
        _bands = [];
        if (_vm is { ShowAccounts: true })
        {
            // Assets stack above zero and liabilities below, each account in its palette slot.
            _bands = [.. _vm.Series.Where(s => !s.IsLiability), .. _vm.Series.Where(s => s.IsLiability)];
            foreach (var band in _bands)
            {
                series.Add(new StackedAreaSeries<double>
                {
                    Name = band.Name,
                    Values = band.Balances.Select(b => ReportFormat.Major(b, band.Currency)).ToList(),
                    Fill = ReportChartKit.Fill(this, band.Slot, 170),
                    Stroke = ReportChartKit.Stroke(this, band.Slot, 1),
                    GeometrySize = 0,
                    LineSmoothness = 0,
                    YToolTipLabelFormatter = p => ReportFormat.Money(band.Balances[p.Index], band.Currency),
                });
            }
        }

        var showBands = _bands.Count > 0;
        series.Add(new LineSeries<double>
        {
            Name = Keel.Desktop.Resources.Strings.Reports_NetWorth_Title,
            Values = rows.Select(r => ReportFormat.Major(r.NetWorth, r.Currency)).ToList(),
            Stroke = ReportChartKit.Stroke(this, showBands ? ChartPalette.InkSlot : 0, 2.5f),
            Fill = showBands ? null : ReportChartKit.Fill(this, 0, 40),
            GeometrySize = 8,
            GeometryFill = ReportChartKit.Fill(this, showBands ? ChartPalette.InkSlot : 0),
            GeometryStroke = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Resolve(this, "Keel.Chart.Surface"))) { StrokeThickness = 2 },
            LineSmoothness = 0,
            YToolTipLabelFormatter = p => rows[p.Index].NetWorthText,
        });
        NetWorthChart.Series = series;
    }
}
