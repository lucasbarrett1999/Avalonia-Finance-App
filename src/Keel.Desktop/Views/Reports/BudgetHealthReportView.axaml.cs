using Avalonia.Controls;
using Keel.Desktop.Controls;
using Keel.Desktop.ViewModels.Reports;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

namespace Keel.Desktop.Views.Reports;

/// <summary>View for <see cref="BudgetHealthReportViewModel"/>: the age-of-money line over month ends.</summary>
public partial class BudgetHealthReportView : UserControl
{
    private BudgetHealthReportViewModel? _vm;

    /// <summary>Creates the view.</summary>
    public BudgetHealthReportView()
    {
        InitializeComponent();
        HealthChart.DataPointerDown += (_, points) => OnPointDown(points.FirstOrDefault());
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

        _vm = DataContext as BudgetHealthReportViewModel;
        if (_vm is not null)
        {
            _vm.ChartChanged += OnChartChanged;
        }

        Rebuild();
    }

    private void OnChartChanged(object? sender, EventArgs e) => Rebuild();

    private void OnPointDown(ChartPoint? point)
    {
        if (point is not null)
        {
            _vm?.OpenPoint(point.Index);
        }
    }

    private void Rebuild()
    {
        ReportChartKit.Configure(HealthChart);
        var rows = _vm?.Rows ?? [];
        HealthChart.XAxes = [ReportChartKit.CategoryAxis(this, rows.Select(r => ReportFormat.ShortMonth(r.Date)).ToList())];
        var axis = ReportChartKit.ValueAxis(this, v => v.ToString("0", System.Globalization.CultureInfo.CurrentCulture));
        axis.MinLimit = 0;
        HealthChart.YAxes = [axis];
        HealthChart.Series =
        [
            new LineSeries<double?>
            {
                Name = Keel.Desktop.Resources.Strings.Health_AgeOfMoney,
                Values = rows.Select(r => r.Days is { } d ? (double?)d : null).ToList(),
                Stroke = ReportChartKit.Stroke(this, 0, 2.5f),
                Fill = ReportChartKit.Fill(this, 0, 40),
                GeometrySize = 8,
                GeometryFill = ReportChartKit.Fill(this, 0),
                GeometryStroke = new SolidColorPaint(ChartPalette.ToSk(ChartPalette.Resolve(this, "Keel.Chart.Surface"))) { StrokeThickness = 2 },
                LineSmoothness = 0,
                YToolTipLabelFormatter = p => rows[p.Index].DaysText,
            },
        ];
    }
}
