using Avalonia.Controls;
using Keel.Desktop.Controls;
using Keel.Desktop.ViewModels.Reports;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

namespace Keel.Desktop.Views.Reports;

/// <summary>View for <see cref="IncomeExpenseReportViewModel"/>: monthly income and expense columns with a net line.</summary>
public partial class IncomeExpenseReportView : UserControl
{
    private IncomeExpenseReportViewModel? _vm;

    /// <summary>Creates the view.</summary>
    public IncomeExpenseReportView()
    {
        InitializeComponent();
        IncomeExpenseChart.DataPointerDown += (chart, points) => OnPointDown(chart, points.FirstOrDefault());
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

        _vm = DataContext as IncomeExpenseReportViewModel;
        if (_vm is not null)
        {
            _vm.ChartChanged += OnChartChanged;
        }

        Rebuild();
    }

    private void OnChartChanged(object? sender, EventArgs e) => Rebuild();

    private void OnPointDown(IChartView chart, ChartPoint? point)
    {
        var series = ReportChartKit.SeriesIndex(IncomeExpenseChart.Series, point);
        if (series >= 0 && point is not null)
        {
            _vm?.OpenPoint(series, point.Index);
        }
    }

    private void Rebuild()
    {
        ReportChartKit.Configure(IncomeExpenseChart);
        var rows = _vm?.Rows ?? [];
        var currency = _vm?.Report?.Currency ?? Keel.Domain.Currency.Default;
        IncomeExpenseChart.XAxes = [ReportChartKit.CategoryAxis(this, rows.Select(r => ReportFormat.ShortMonth(r.Month)).ToList())];
        IncomeExpenseChart.YAxes = [ReportChartKit.ValueAxis(this, v => ReportFormat.Axis(v, currency))];
        IncomeExpenseChart.Series =
        [
            new ColumnSeries<double>
            {
                Name = Keel.Desktop.Resources.Strings.Reports_Income,
                Values = rows.Select(r => ReportFormat.Major(r.Income, r.Currency)).ToList(),
                Fill = ReportChartKit.Fill(this, 0),
                Stroke = null,
                Rx = 3,
                Ry = 3,
                MaxBarWidth = 22,
                Padding = 2,
                YToolTipLabelFormatter = p => rows[p.Index].IncomeText,
            },
            new ColumnSeries<double>
            {
                Name = Keel.Desktop.Resources.Strings.Reports_Expense,
                Values = rows.Select(r => ReportFormat.Major(r.Expense, r.Currency)).ToList(),
                Fill = ReportChartKit.Fill(this, 1),
                Stroke = null,
                Rx = 3,
                Ry = 3,
                MaxBarWidth = 22,
                Padding = 2,
                YToolTipLabelFormatter = p => rows[p.Index].ExpenseText,
            },
            new LineSeries<double>
            {
                Name = Keel.Desktop.Resources.Strings.Reports_Net,
                Values = rows.Select(r => ReportFormat.Major(r.Net, r.Currency)).ToList(),
                Stroke = ReportChartKit.Stroke(this, ChartPalette.InkSlot, 2),
                Fill = null,
                GeometrySize = 8,
                GeometryFill = ReportChartKit.Fill(this, ChartPalette.InkSlot),
                GeometryStroke = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Resolve(this, "Keel.Chart.Surface"))) { StrokeThickness = 2 },
                LineSmoothness = 0,
                YToolTipLabelFormatter = p => rows[p.Index].NetText,
            },
        ];
    }
}
