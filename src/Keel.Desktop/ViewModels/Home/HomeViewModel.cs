using System.Data.Common;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Forecast;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Navigation;
using Keel.Application.Recurring;
using Keel.Application.Reports;
using Keel.Application.Scheduling;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Home;
using Keel.Desktop.ViewModels.Reports;
using Keel.Domain;
using Keel.Domain.Budgeting;

// The page view model stays in Keel.Desktop.ViewModels so the ViewLocator maps it to Views/HomeView.
namespace Keel.Desktop.ViewModels;

/// <summary>
/// The Home dashboard (PRD 9.2, F-DASH-1): Ready to Assign, review queue, accounts overview,
/// budget alerts, and a 12-month net-worth sparkline, each linking to its screen. Upcoming bills
/// and the forecast low point arrive with recurring detection (M5) and show that state until then.
/// Every card refreshes on <see cref="LedgerChanged"/> and <see cref="BudgetChanged"/>.
/// </summary>
public sealed partial class HomeViewModel : PageViewModel, INavigationTarget, IRecipient<LedgerChanged>, IRecipient<BudgetChanged>, IRecipient<RecurringChanged>
{
    /// <summary>Number of budget alerts shown.</summary>
    public const int AlertCount = 5;

    private readonly IBudgetService _budget;
    private readonly IRegisterQuery _register;
    private readonly IAccountService _accounts;
    private readonly IReportService _reports;
    private readonly INavigationService _navigation;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private readonly TimeProvider _time;
    private int _version;

    /// <summary>Creates the dashboard.</summary>
    public HomeViewModel(
        IBudgetService budget,
        IRegisterQuery register,
        IAccountService accounts,
        IReportService reports,
        INavigationService navigation,
        DialogService dialogs,
        StatusService status,
        AppSession session,
        TimeProvider time,
        IMessenger messenger,
        IRecurringService? recurring = null,
        IScheduledTransactionService? scheduled = null,
        IForecastService? forecast = null)
    {
        _recurring = recurring;
        _scheduled = scheduled;
        _forecast = forecast;
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(messenger);
        _budget = budget;
        _register = register;
        _accounts = accounts;
        _reports = reports;
        _navigation = navigation;
        _dialogs = dialogs;
        _status = status;
        _time = time;
        FileName = session.BudgetFile?.FileName ?? Strings.Shell_NoFile;
        FilePath = session.BudgetFile?.Path ?? string.Empty;
        messenger.Register<LedgerChanged>(this);
        messenger.Register<BudgetChanged>(this);
        messenger.Register<RecurringChanged>(this);
    }

    /// <inheritdoc />
    public override string Title => Strings.Page_Home_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Page_Home_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => Strings.Page_Home_EmptyHeading;

    /// <inheritdoc />
    public override string EmptyMessage => Strings.Page_Home_EmptyMessage;

    /// <summary>Name of the open budget file.</summary>
    public string FileName { get; }

    /// <summary>Full path of the open budget file.</summary>
    public string FilePath { get; }

    /// <summary>Label for the budget file line.</summary>
    public string FileLabel => Strings.Page_Home_FileLabel;

    /// <summary>The current refresh (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Whether the first load finished.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowDashboard))]
    public partial bool IsInitialized { get; private set; }

    /// <summary>Whether any account exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowDashboard))]
    public partial bool HasAccounts { get; private set; }

    /// <summary>The designed empty state ("Add an account to begin").</summary>
    public bool ShowEmptyState => IsInitialized && !HasAccounts;

    /// <summary>The card grid.</summary>
    public bool ShowDashboard => IsInitialized && HasAccounts;

    /// <summary>Load error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Whether loading failed.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Ready to Assign this month (minor units).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadyToAssignText), nameof(IsReadyToAssignNegative), nameof(ReadyToAssignHint))]
    public partial Money ReadyToAssign { get; private set; }

    /// <summary>Ready to Assign text.</summary>
    public string ReadyToAssignText => ReadyToAssign.Currency is null ? string.Empty : ReportFormat.Money(ReadyToAssign.Amount, ReadyToAssign.Currency);

    /// <summary>Assigned more than there is (red, with a warning line).</summary>
    public bool IsReadyToAssignNegative => ReadyToAssign.Amount < 0;

    /// <summary>A line under the number that explains it.</summary>
    public string ReadyToAssignHint => ReadyToAssign.Amount switch
    {
        < 0 => Strings.Home_ReadyToAssignNegative,
        0 => Strings.Home_ReadyToAssignZero,
        _ => Strings.Home_ReadyToAssignPositive,
    };

    /// <summary>"September 2026".</summary>
    [ObservableProperty]
    public partial string MonthText { get; private set; } = string.Empty;

    /// <summary>Unapproved transactions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReviewText), nameof(HasReview))]
    public partial int ReviewCount { get; private set; }

    /// <summary>Whether anything waits for review.</summary>
    public bool HasReview => ReviewCount > 0;

    /// <summary>"12 transactions to review" or the caught-up line.</summary>
    public string ReviewText => ReviewCount switch
    {
        0 => Strings.Home_ReviewNone,
        1 => Strings.Home_ReviewOne,
        _ => LedgerText.Format(Strings.Home_ReviewMany, ReviewCount),
    };

    /// <summary>Accounts overview by sidebar group.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HomeAccountGroup> AccountGroups { get; private set; } = [];

    /// <summary>Top overspent and underfunded categories this month.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlerts))]
    public partial IReadOnlyList<HomeBudgetAlert> Alerts { get; private set; } = [];

    /// <summary>Whether any alert exists.</summary>
    public bool HasAlerts => Alerts.Count > 0;

    /// <summary>Net worth at the last 12 month ends (major units, for the sparkline).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNetWorthTrend))]
    public partial IReadOnlyList<double> NetWorthValues { get; private set; } = [];

    /// <summary>Whether the sparkline has at least two points.</summary>
    public bool HasNetWorthTrend => NetWorthValues.Count > 1;

    /// <summary>Latest net worth.</summary>
    [ObservableProperty]
    public partial string NetWorthText { get; private set; } = string.Empty;

    /// <summary>Latest net worth is negative.</summary>
    [ObservableProperty]
    public partial bool IsNetWorthNegative { get; private set; }

    /// <summary>Change over the sparkline's range.</summary>
    [ObservableProperty]
    public partial string NetWorthChangeText { get; private set; } = string.Empty;

    /// <inheritdoc />
    public void OnNavigatedTo(object? parameter) => Loading = LoadAsync();

    /// <inheritdoc />
    public void Receive(LedgerChanged message) => Dispatcher.UIThread.Post(Refresh);

    /// <inheritdoc />
    public void Receive(BudgetChanged message) => Dispatcher.UIThread.Post(Refresh);

    /// <summary>Opens Budget to assign Ready to Assign.</summary>
    [RelayCommand]
    public void Assign() => _navigation.NavigateTo<BudgetViewModel>();

    /// <summary>Opens the Review queue.</summary>
    [RelayCommand]
    public void StartReview() => _navigation.NavigateTo<ReviewViewModel>();

    /// <summary>Opens the Net worth report.</summary>
    [RelayCommand]
    public void OpenNetWorth() => _navigation.NavigateTo<ReportsViewModel>(ReportKind.NetWorth);

    /// <summary>Opens an account's register.</summary>
    [RelayCommand]
    public void OpenAccount(Guid id) => _navigation.NavigateTo<AccountsViewModel>(id);

    /// <summary>Opens the All Accounts register.</summary>
    [RelayCommand]
    public void OpenAccounts() => _navigation.NavigateTo<AccountsViewModel>();

    /// <summary>Adds the first account (empty state) and opens its register.</summary>
    [RelayCommand]
    public async Task AddAccountAsync()
    {
        var dialog = new AccountEditorViewModel(_accounts);
        if (await _dialogs.ShowAsync(dialog) && dialog.Result is { } created)
        {
            _status.Show(LedgerText.Format(Strings.Status_AccountAdded, created.Name), offerUndo: true);
            OpenAccount(created.Id);
        }
    }

    private void Refresh()
    {
        if (IsInitialized)
        {
            Loading = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        var version = ++_version;
        try
        {
            var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
            var month = BudgetMonth.Of(today);
            var accountsTask = _accounts.GetAccountsAsync(includeClosed: false, CancellationToken.None);
            var budgetTask = _budget.GetMonthAsync(month, CancellationToken.None);
            var summaryTask = _register.GetSummaryAsync(null, CancellationToken.None);
            var netWorthTask = _reports.GetNetWorthAsync(new ReportQuery(month.AddMonths(-11), today, IncludeTracking: true), CancellationToken.None);
            var accounts = await accountsTask;
            var budget = await budgetTask;
            var summary = await summaryTask;
            var netWorth = await netWorthTask;
            if (version != _version)
            {
                return;
            }

            ErrorMessage = null;
            HasAccounts = accounts.Count > 0;
            MonthText = LedgerText.Format(Strings.Home_ReadyToAssignMonth, month.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
            ReadyToAssign = budget.ReadyToAssign;
            ReviewCount = summary.UnapprovedCount;
            AccountGroups = Enum.GetValues<AccountGroup>()
                .Select(g => (Group: g, Items: accounts.Where(a => a.Group == g).ToList()))
                .Where(g => g.Items.Count > 0)
                .Select(g => new HomeAccountGroup(
                    LedgerText.Group(g.Group),
                    g.Items.Sum(a => a.Balance.Amount),
                    g.Items[0].Balance.Currency,
                    [.. g.Items.Select(a => new HomeAccountRow(a.Id, a.Name, a.Balance.Amount, a.Balance.Currency, OpenAccountCommand))]))
                .ToList();
            Alerts = BuildAlerts(budget);
            NetWorthValues = netWorth.Points.Select(p => ReportFormat.Major(p.NetWorth, netWorth.Currency)).ToList();
            if (netWorth.Points.Count > 0)
            {
                var last = netWorth.Points[^1];
                var change = last.NetWorth - netWorth.Points[0].NetWorth;
                NetWorthText = ReportFormat.Money(last.NetWorth, netWorth.Currency);
                IsNetWorthNegative = last.NetWorth < 0;
                NetWorthChangeText = LedgerText.Format(change >= 0 ? Strings.Reports_NetWorthUp : Strings.Reports_NetWorthDown,
                    ReportFormat.Money(Math.Abs(change), netWorth.Currency),
                    netWorth.Points[0].Date.ToString("MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture));
            }

            await LoadRecurringCardsAsync(today, version);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException)
        {
            ErrorMessage = LedgerText.Format(Strings.Home_ErrorLoading, ex.Message);
        }
        finally
        {
            if (version == _version)
            {
                IsInitialized = true;
            }
        }
    }

    private static List<HomeBudgetAlert> BuildAlerts(BudgetMonthDto month)
    {
        var categories = month.Groups.Where(g => !g.IsHidden).SelectMany(g => g.Categories).Where(c => !c.IsHidden).ToList();
        var overspent = categories
            .Where(c => c.Available.Amount < 0)
            .OrderBy(c => c.Available.Amount)
            .Select(c => new HomeBudgetAlert(c.Id, c.Name, true, Strings.Home_AlertOverspent, -c.Available.Amount, c.Available.Currency));
        var underfunded = categories
            .Where(c => c.Available.Amount >= 0 && c.Target is { Underfunded.Amount: > 0 })
            .OrderByDescending(c => c.Target!.Underfunded.Amount)
            .Select(c => new HomeBudgetAlert(c.Id, c.Name, false, Strings.Home_AlertUnderfunded, c.Target!.Underfunded.Amount, c.Available.Currency));
        return overspent.Concat(underfunded).Take(AlertCount).ToList();
    }
}
