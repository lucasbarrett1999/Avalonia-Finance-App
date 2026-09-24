using Avalonia;
using Avalonia.Controls;
using Keel.Desktop.Controls;
using Keel.Desktop.ViewModels.Bills;
using Keel.Desktop.ViewModels.Reports;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Avalonia;

namespace Keel.Desktop.Views.Bills;

/// <summary>
/// The Bills detail panel's amount history (PRD 9.6): one column per past occurrence, in the report
/// chart style; the table under it lists the same values.
/// </summary>
public sealed class BillHistoryChart : UserControl
{
    /// <summary>Defines <see cref="History"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<BillHistoryPoint>?> HistoryProperty =
        AvaloniaProperty.Register<BillHistoryChart, IReadOnlyList<BillHistoryPoint>?>(nameof(History));

    private readonly CartesianChart _chart = new() { Name = "HistoryChart" };

    /// <summary>Creates the chart.</summary>
    public BillHistoryChart()
    {
        Content = _chart;
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    /// <summary>Past occurrences, oldest first.</summary>
    public IReadOnlyList<BillHistoryPoint>? History
    {
        get => GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    /// <summary>The inner chart (tests).</summary>
    public CartesianChart Chart => _chart;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == HistoryProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        ReportChartKit.Configure(_chart);
        var history = History ?? [];
        var currency = history.Count > 0 ? history[0].Currency : Keel.Domain.Currency.Default;
        _chart.XAxes = [ReportChartKit.CategoryAxis(this, history.Select(h => h.Date.ToString("MMM yy", System.Globalization.CultureInfo.CurrentCulture)).ToList())];
        var axis = ReportChartKit.ValueAxis(this, v => ReportFormat.Axis(v, currency));
        axis.MinLimit = 0;
        _chart.YAxes = [axis];
        _chart.Series = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Name = Keel.Desktop.Resources.Strings.Bills_AmountHistory,
                Values = history.Select(h => ReportFormat.Major(Math.Abs(h.Amount), h.Currency)).ToList(),
                Fill = ReportChartKit.Fill(this, 0),
                MaxBarWidth = 18,
                YToolTipLabelFormatter = p => history[p.Index].AmountText,
            },
        };
    }
}
