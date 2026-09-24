using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Reports;

namespace Keel.Desktop.ViewModels.Reports;

/// <summary>The reports in the left list of the Reports screen (PRD 9.8).</summary>
public enum ReportKind
{
    /// <summary>Spending by group and category (F-REP-1).</summary>
    Spending,

    /// <summary>Income versus expense (F-REP-2).</summary>
    IncomeExpense,

    /// <summary>Net worth (F-REP-3).</summary>
    NetWorth,
}

/// <summary>
/// One report under the shared toolbar: loads its data for a <see cref="ReportQuery"/>, exposes
/// chart data and a table, drills down through <see cref="ReportsViewModel"/>, and exports its
/// table as CSV. The view rebuilds its chart on <see cref="ChartChanged"/>.
/// </summary>
public abstract partial class ReportViewModel : ViewModelBase
{
    /// <summary>Creates the report for its owner screen.</summary>
    protected ReportViewModel(ReportsViewModel owner, bool includeTransfers, bool includeTracking)
    {
        Owner = owner;
        IncludeTransfers = includeTransfers;
        IncludeTracking = includeTracking;
    }

    /// <summary>Raised when the chart data changed (after a load or a drill).</summary>
    public event EventHandler? ChartChanged;

    /// <summary>Which report this is.</summary>
    public abstract ReportKind Kind { get; }

    /// <summary>Name in the report list.</summary>
    public abstract string Title { get; }

    /// <summary>One line under the name.</summary>
    public abstract string Description { get; }

    /// <summary>Icon resource key.</summary>
    public abstract string IconKey { get; }

    /// <summary>Whether the "include transfers" toggle applies.</summary>
    public virtual bool SupportsTransfers => true;

    /// <summary>Count categorized transfers (F-REP-1 toggle).</summary>
    [ObservableProperty]
    public partial bool IncludeTransfers { get; set; }

    /// <summary>Include tracking accounts (F-REP-1 toggle).</summary>
    [ObservableProperty]
    public partial bool IncludeTracking { get; set; }

    /// <summary>Whether the loaded range has anything to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasData { get; protected set; }

    /// <summary>Whether a load finished with nothing to show (the report's own empty state).</summary>
    public bool IsEmpty => IsLoaded && !HasData;

    /// <summary>Whether data has been loaded at least once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoaded { get; protected set; }

    /// <summary>The query of the last load.</summary>
    public ReportQuery? Query { get; private set; }

    /// <summary>Suggested CSV file name.</summary>
    public abstract string CsvFileName { get; }

    /// <summary>The screen that hosts the report (drill-down and navigation).</summary>
    protected ReportsViewModel Owner { get; }

    /// <summary>Loads the report for <paramref name="query"/>.</summary>
    public async Task LoadAsync(IReportService service, ReportQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(query);
        await LoadCoreAsync(service, query, ct);
        Query = query;
        IsLoaded = true;
        RaiseChartChanged();
    }

    /// <summary>The report table as CSV.</summary>
    public abstract string ToCsv();

    /// <summary>Loads and maps the data.</summary>
    protected abstract Task LoadCoreAsync(IReportService service, ReportQuery query, CancellationToken ct);

    /// <summary>Tells the view to rebuild the chart.</summary>
    protected void RaiseChartChanged() => ChartChanged?.Invoke(this, EventArgs.Empty);

    partial void OnIncludeTransfersChanged(bool value) => Owner.OptionsChanged(this);

    partial void OnIncludeTrackingChanged(bool value) => Owner.OptionsChanged(this);
}
