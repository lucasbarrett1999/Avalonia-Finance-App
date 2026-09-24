using Avalonia.Controls;
using Keel.Desktop.Controls;
using Keel.Desktop.ViewModels.Reports;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;

namespace Keel.Desktop.Views.Reports;

/// <summary>View for <see cref="SpendingReportViewModel"/>: builds the donut from the view model's slices.</summary>
public partial class SpendingReportView : UserControl
{
    private SpendingReportViewModel? _vm;

    /// <summary>Creates the view.</summary>
    public SpendingReportView()
    {
        InitializeComponent();
        ReportChartKit.Configure(SpendingChart);
        SpendingChart.DataPointerDown += (chart, points) => OnPointDown(chart, points.FirstOrDefault());
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

        _vm = DataContext as SpendingReportViewModel;
        if (_vm is not null)
        {
            _vm.ChartChanged += OnChartChanged;
        }

        Rebuild();
    }

    private void OnChartChanged(object? sender, EventArgs e) => Rebuild();

    private void OnPointDown(IChartView chart, ChartPoint? point)
    {
        var index = ReportChartKit.SeriesIndex(SpendingChart.Series, point);
        if (index >= 0)
        {
            _vm?.OpenSlice(index);
        }
    }

    private void Rebuild()
    {
        ReportChartKit.Configure(SpendingChart);
        var slices = _vm?.Slices ?? [];
        SpendingChart.Series = slices.Select(slice => (ISeries)new PieSeries<double>
        {
            Values = [ReportFormat.Major(slice.Amount, slice.Currency)],
            Name = slice.Name,
            Fill = ReportChartKit.Fill(this, slice.Slot),
            Stroke = ReportChartKit.Gap(this),
            InnerRadius = 70,
            HoverPushout = 6,
            ToolTipLabelFormatter = _ => $"{slice.AmountText} ({slice.ShareText})",
        }).ToList();
    }
}
