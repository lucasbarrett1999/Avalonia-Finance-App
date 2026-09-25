using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Accounts;
using Keel.Application.Messaging;
using Keel.Application.Navigation;
using Keel.Application.Reports;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Register;
using Keel.Desktop.ViewModels.Reports;
using Keel.Domain.Budgeting;

// The page view model stays in Keel.Desktop.ViewModels so the ViewLocator maps it to Views/ReportsView.
namespace Keel.Desktop.ViewModels;

/// <summary>Date-range presets of the report toolbar.</summary>
public enum ReportRange
{
    /// <summary>This calendar month.</summary>
    ThisMonth,

    /// <summary>Last calendar month.</summary>
    LastMonth,

    /// <summary>This month and the two before.</summary>
    LastThreeMonths,

    /// <summary>This month and the five before.</summary>
    LastSixMonths,

    /// <summary>This month and the eleven before.</summary>
    LastTwelveMonths,

    /// <summary>This calendar year.</summary>
    ThisYear,

    /// <summary>Last calendar year.</summary>
    LastYear,

    /// <summary>Custom dates.</summary>
    Custom,
}

/// <summary>An account in the report accounts filter.</summary>
public sealed partial class ReportAccountOption(Guid id, string name, string group, Action changed) : ObservableObject
{
    /// <summary>Account.</summary>
    public Guid Id { get; } = id;

    /// <summary>Name.</summary>
    public string Name { get; } = name;

    /// <summary>Sidebar group name.</summary>
    public string Group { get; } = group;

    /// <summary>Whether the account is included (none checked means every account).</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>
/// Reports (PRD 9.8, F-REP-1..3): a list of reports on the left and a shared toolbar (date range
/// presets and custom dates, accounts filter, include transfers and tracking toggles, CSV export)
/// above the selected report. Every chart element drills down to the underlying transactions.
/// Reloads on <see cref="LedgerChanged"/>. Navigate with a <see cref="ReportKind"/> to open a report.
/// </summary>
public sealed partial class ReportsViewModel : PageViewModel, INavigationTarget, IRecipient<LedgerChanged>
{
    private readonly IReportService _reports;
    private readonly IAccountService _accounts;
    private readonly INavigationService _navigation;
    private readonly StatusService _status;
    private readonly TimeProvider _time;
    private readonly IFileDialogs? _files;
    private CancellationTokenSource? _loadCts;
    private bool _suppressReload;

    /// <summary>Creates the screen.</summary>
    public ReportsViewModel(IReportService reports, IAccountService accounts, INavigationService navigation, StatusService status, TimeProvider time, IMessenger messenger, Keel.Application.Forecast.IForecastService? forecast = null, IFileDialogs? files = null)
    {
        _files = files;
        ArgumentNullException.ThrowIfNull(messenger);
        _reports = reports;
        _accounts = accounts;
        _navigation = navigation;
        _status = status;
        _time = time;
        Reports = [new SpendingReportViewModel(this), new IncomeExpenseReportViewModel(this), new NetWorthReportViewModel(this)];
        if (forecast is not null)
        {
            Reports = [.. Reports, new ForecastReportViewModel(this, forecast, time)];
        }

        Reports = [.. Reports, new BudgetHealthReportViewModel(this, time)];

        Ranges = Enum.GetValues<ReportRange>().Select(r => new Choice<ReportRange>(r, Strings.ResourceManager.GetString("ReportRange_" + r, Strings.Culture) ?? r.ToString())).ToList();
        _suppressReload = true;
        SelectedRange = Ranges.First(r => r.Value == ReportRange.LastTwelveMonths);
        SelectedReport = Reports[0];
        _suppressReload = false;
        messenger.Register(this);
    }

    /// <inheritdoc />
    public override string Title => Strings.Page_Reports_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Page_Reports_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => Strings.Page_Reports_EmptyHeading;

    /// <inheritdoc />
    public override string EmptyMessage => Strings.Page_Reports_EmptyMessage;

    /// <summary>The report list.</summary>
    public IReadOnlyList<ReportViewModel> Reports { get; }

    /// <summary>The selected report.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomRangeShown))]
    public partial ReportViewModel SelectedReport { get; set; }

    /// <summary>Date-range presets.</summary>
    public IReadOnlyList<Choice<ReportRange>> Ranges { get; }

    /// <summary>The selected preset.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomRange), nameof(IsCustomRangeShown))]
    public partial Choice<ReportRange> SelectedRange { get; set; }

    /// <summary>Whether the custom date pickers show.</summary>
    public bool IsCustomRange => SelectedRange?.Value == ReportRange.Custom;

    /// <summary>Whether the custom date pickers show for the selected report.</summary>
    public bool IsCustomRangeShown => IsCustomRange && SelectedReport?.SupportsRange != false;

    /// <summary>Custom range start.</summary>
    [ObservableProperty]
    public partial DateTime? CustomFrom { get; set; }

    /// <summary>Custom range end.</summary>
    [ObservableProperty]
    public partial DateTime? CustomTo { get; set; }

    /// <summary>The effective range text under the toolbar.</summary>
    [ObservableProperty]
    public partial string RangeText { get; private set; } = string.Empty;

    /// <summary>Accounts for the filter.</summary>
    public ObservableCollection<ReportAccountOption> AccountOptions { get; } = [];

    /// <summary>"All accounts" or "2 accounts".</summary>
    [ObservableProperty]
    public partial string AccountsLabel { get; private set; } = Strings.Reports_AllAccounts;

    /// <summary>Whether any account exists (otherwise the page shows its empty state).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowReports))]
    public partial bool HasAccounts { get; private set; }

    /// <summary>Whether the first load finished.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowReports))]
    public partial bool IsInitialized { get; private set; }

    /// <summary>Whether a load is running.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Load error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Whether loading failed.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>The designed empty state (no accounts yet).</summary>
    public bool ShowEmptyState => IsInitialized && !HasAccounts;

    /// <summary>The report area.</summary>
    public bool ShowReports => IsInitialized && HasAccounts;

    /// <summary>The current load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Saves exported CSV text; set by the view to a save-file picker. Returns the saved file
    /// name, or null when cancelled.
    /// </summary>
    public Func<string, string, Task<string?>>? SaveFile { get; set; }

    /// <summary>
    /// Renders the selected report's chart to a PNG file at twice its size (PRD 9.8); set by the view.
    /// Returns false when the report shows no chart.
    /// </summary>
    public Func<string, bool>? RenderChartPng { get; set; }

    /// <summary>The only account in the filter, when exactly one is selected (drill-down opens its register).</summary>
    public Guid? SingleAccountId => SelectedAccountIds() is { Count: 1 } ids ? ids.First() : null;

    /// <inheritdoc />
    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is ReportKind kind)
        {
            _suppressReload = true;
            SelectedReport = Reports.First(r => r.Kind == kind);
            _suppressReload = false;
        }

        Loading = LoadAsync(reloadAccounts: true);
    }

    /// <inheritdoc />
    public void Receive(LedgerChanged message) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (IsInitialized)
            {
                Loading = LoadAsync(reloadAccounts: true);
            }
        });

    /// <summary>The query of the toolbar for <paramref name="report"/>.</summary>
    public ReportQuery CurrentQuery(ReportViewModel report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var (from, to) = CurrentRange();
        return new ReportQuery(from, to, SelectedAccountIds(), report.IncludeTransfers, report.IncludeTracking);
    }

    /// <summary>First and last day of the toolbar range.</summary>
    public (DateOnly From, DateOnly To) CurrentRange()
    {
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var month = BudgetMonth.Of(today);
        var end = month.AddMonths(1).AddDays(-1);
        return SelectedRange?.Value switch
        {
            ReportRange.ThisMonth => (month, end),
            ReportRange.LastMonth => (month.AddMonths(-1), month.AddDays(-1)),
            ReportRange.LastThreeMonths => (month.AddMonths(-2), end),
            ReportRange.LastSixMonths => (month.AddMonths(-5), end),
            ReportRange.ThisYear => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            ReportRange.LastYear => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
            ReportRange.Custom => CustomDates(month, end),
            _ => (month.AddMonths(-11), end),
        };
    }

    /// <summary>Sets a custom range (drill-down from a month, tests).</summary>
    public void SetCustomRange(DateOnly from, DateOnly to)
    {
        _suppressReload = true;
        CustomFrom = from.ToDateTime(TimeOnly.MinValue);
        CustomTo = to.ToDateTime(TimeOnly.MinValue);
        SelectedRange = Ranges.First(r => r.Value == ReportRange.Custom);
        _suppressReload = false;
        Reload();
    }

    /// <summary>Opens the register (drill-down to the underlying transactions).</summary>
    public void OpenRegister(RegisterNavigation navigation) => _navigation.NavigateTo<AccountsViewModel>(navigation);

    /// <summary>Opens Budget (budget health links there).</summary>
    public void OpenBudget() => _navigation.NavigateTo<BudgetViewModel>();

    /// <summary>Opens the Spending report for a range (expense bar drill-down).</summary>
    public void ShowSpending(DateOnly from, DateOnly to, bool includeTransfers, bool includeTracking)
    {
        var spending = Reports.OfType<SpendingReportViewModel>().Single();
        _suppressReload = true;
        spending.IncludeTransfers = includeTransfers;
        spending.IncludeTracking = includeTracking;
        SelectedReport = spending;
        _suppressReload = false;
        SetCustomRange(from, to);
    }

    /// <summary>Called by a report when its toggles change.</summary>
    internal void OptionsChanged(ReportViewModel report)
    {
        if (ReferenceEquals(report, SelectedReport))
        {
            Reload();
        }
    }

    /// <summary>Reloads the selected report.</summary>
    [RelayCommand]
    public void Reload()
    {
        if (!_suppressReload && IsInitialized)
        {
            Loading = LoadAsync(reloadAccounts: false);
        }
    }

    /// <summary>Clears the accounts filter.</summary>
    [RelayCommand]
    public void SelectAllAccounts()
    {
        _suppressReload = true;
        foreach (var option in AccountOptions)
        {
            option.IsChecked = false;
        }

        _suppressReload = false;
        AccountsChanged();
    }

    /// <summary>Exports the selected report's table as CSV (F-REP-2, F-REP-6).</summary>
    [RelayCommand]
    public async Task ExportCsvAsync()
    {
        if (SaveFile is null || !SelectedReport.IsLoaded)
        {
            return;
        }

        try
        {
            var saved = await SaveFile(SelectedReport.CsvFileName, SelectedReport.ToCsv());
            if (saved is not null)
            {
                _status.Show(LedgerText.Format(Strings.Reports_Exported, saved));
            }
        }
        catch (IOException ex)
        {
            _status.Show(LedgerText.Format(Strings.Reports_ExportFailed, ex.Message), isError: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            _status.Show(LedgerText.Format(Strings.Reports_ExportFailed, ex.Message), isError: true);
        }
    }

    /// <summary>Exports the selected report's chart as a PNG image at 2x (PRD 9.8).</summary>
    [RelayCommand]
    public async Task ExportPngAsync()
    {
        if (_files is null || RenderChartPng is null || !SelectedReport.IsLoaded)
        {
            return;
        }

        if (!SelectedReport.HasData)
        {
            _status.Show(Strings.ExportPng_NoChart, isError: true);
            return;
        }

        var path = await _files.SavePngAsync(SelectedReport.PngFileName);
        if (path is null)
        {
            return;
        }

        try
        {
            if (RenderChartPng(path))
            {
                _status.Show(LedgerText.Format(Strings.ExportPng_Saved, Path.GetFileName(path)));
            }
            else
            {
                _status.Show(Strings.ExportPng_NoChart, isError: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Show(LedgerText.Format(Strings.ExportPng_Failed, ex.Message), isError: true);
        }
    }

    partial void OnSelectedReportChanged(ReportViewModel value) => Reload();

    partial void OnSelectedRangeChanged(Choice<ReportRange> value)
    {
        if (value?.Value == ReportRange.Custom && CustomFrom is null)
        {
            _suppressReload = true;
            var (from, to) = (BudgetMonth.Of(DateOnly.FromDateTime(_time.GetLocalNow().DateTime)).AddMonths(-2), DateOnly.FromDateTime(_time.GetLocalNow().DateTime));
            CustomFrom = from.ToDateTime(TimeOnly.MinValue);
            CustomTo = to.ToDateTime(TimeOnly.MinValue);
            _suppressReload = false;
        }

        Reload();
    }

    partial void OnCustomFromChanged(DateTime? value) => Reload();

    partial void OnCustomToChanged(DateTime? value) => Reload();

    private (DateOnly From, DateOnly To) CustomDates(DateOnly month, DateOnly end)
    {
        var from = CustomFrom is { } f ? DateOnly.FromDateTime(f) : month;
        var to = CustomTo is { } t ? DateOnly.FromDateTime(t) : end;
        return to < from ? (to, from) : (from, to);
    }

    private IReadOnlyCollection<Guid>? SelectedAccountIds()
    {
        var ids = AccountOptions.Where(a => a.IsChecked).Select(a => a.Id).ToList();
        return ids.Count == 0 ? null : ids;
    }

    private void AccountsChanged()
    {
        if (_suppressReload)
        {
            return;
        }

        var count = AccountOptions.Count(a => a.IsChecked);
        AccountsLabel = count == 0 || count == AccountOptions.Count
            ? Strings.Reports_AllAccounts
            : count == 1 ? AccountOptions.First(a => a.IsChecked).Name : LedgerText.Format(Strings.Reports_AccountCount, count);
        Reload();
    }

    private async Task LoadAsync(bool reloadAccounts)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            if (reloadAccounts)
            {
                var accounts = await _accounts.GetAccountsAsync(includeClosed: true, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                HasAccounts = accounts.Count > 0;
                var selected = AccountOptions.Where(a => a.IsChecked).Select(a => a.Id).ToHashSet();
                if (!AccountOptions.Select(a => a.Id).SequenceEqual(accounts.Select(a => a.Id)))
                {
                    _suppressReload = true;
                    AccountOptions.Clear();
                    foreach (var account in accounts)
                    {
                        AccountOptions.Add(new ReportAccountOption(account.Id, account.Name, LedgerText.Group(account.Group), AccountsChanged) { IsChecked = selected.Contains(account.Id) });
                    }

                    _suppressReload = false;
                }
            }

            var (from, to) = CurrentRange();
            RangeText = SelectedReport.RangeTextOverride ?? ReportFormat.Range(from, to);
            if (HasAccounts)
            {
                var report = SelectedReport;
                await report.LoadAsync(_reports, CurrentQuery(report), cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            ErrorMessage = LedgerText.Format(Strings.Reports_ErrorLoading, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
                IsInitialized = true;
            }
        }
    }
}
