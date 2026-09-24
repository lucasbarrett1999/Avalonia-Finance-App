using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Accounts;
using Keel.Application.Alerts;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Navigation;
using Keel.Application.Payees;
using Keel.Application.Recurring;
using Keel.Application.Scheduling;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Alerts;
using Keel.Desktop.ViewModels.Bills;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Domain.Recurring;

// The page view model stays in Keel.Desktop.ViewModels so the ViewLocator maps it to Views/BillsView.
namespace Keel.Desktop.ViewModels;

/// <summary>The Bills tabs (PRD 9.6).</summary>
public enum BillsTab
{
    /// <summary>Month grid with amounts on days.</summary>
    Calendar,

    /// <summary>Sortable, filterable list.</summary>
    List,

    /// <summary>Subscriptions with totals and price changes.</summary>
    Subscriptions,
}

/// <summary>List filter of the Bills screen.</summary>
public enum BillsFilter
{
    /// <summary>Everything but dismissed.</summary>
    All,

    /// <summary>Confirmed.</summary>
    Active,

    /// <summary>Detected, awaiting confirmation.</summary>
    Detected,

    /// <summary>Paused or ended.</summary>
    Paused,

    /// <summary>Dismissed (detection can be re-enabled).</summary>
    Dismissed,
}

/// <summary>A category group the user can mark as holding subscriptions (F-REC-2).</summary>
public sealed partial class SubscriptionGroupOption(Guid id, string name, Action changed) : ObservableObject
{
    /// <summary>Group.</summary>
    public Guid Id { get; } = id;

    /// <summary>Name.</summary>
    public string Name { get; } = name;

    /// <summary>Designated as a subscription group.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>
/// Bills and subscriptions (PRD 9.6, F-REC-1..4): totals, a Calendar / List / Subscriptions tab strip, and a
/// detail panel with the amount history chart, confirm / edit / pause / dismiss, "create scheduled
/// transaction", "create target" and the item's alerts. "Run detection now" runs detection on demand.
/// Navigate with a recurring item id to select it. Refreshes on <see cref="LedgerChanged"/>,
/// <see cref="RecurringChanged"/> and <see cref="AlertsChanged"/>.
/// </summary>
public sealed partial class BillsViewModel : PageViewModel, INavigationTarget, IRecipient<LedgerChanged>, IRecipient<RecurringChanged>, IRecipient<AlertsChanged>
{
    private readonly IRecurringService _recurring;
    private readonly IScheduledTransactionService _scheduled;
    private readonly IAlertService _alerts;
    private readonly IAccountService _accounts;
    private readonly ICategoryService _categories;
    private readonly IPayeeService _payees;
    private readonly INavigationService _navigation;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private readonly ScheduleEditorLauncher _schedules;
    private readonly TimeProvider _time;
    private List<BillItemViewModel> _all = [];
    private int _version;
    private bool _suppressDesignations;
    private Guid? _selectAfterLoad;

    /// <summary>Creates the screen.</summary>
    public BillsViewModel(
        IRecurringService recurring,
        IScheduledTransactionService scheduled,
        IAlertService alerts,
        IAccountService accounts,
        ICategoryService categories,
        IPayeeService payees,
        INavigationService navigation,
        DialogService dialogs,
        StatusService status,
        ScheduleEditorLauncher schedules,
        TimeProvider time,
        IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(messenger);
        _recurring = recurring;
        _scheduled = scheduled;
        _alerts = alerts;
        _accounts = accounts;
        _categories = categories;
        _payees = payees;
        _navigation = navigation;
        _dialogs = dialogs;
        _status = status;
        _schedules = schedules;
        _time = time;
        Filters = Enum.GetValues<BillsFilter>().Select(f => new Choice<BillsFilter>(f, Strings.ResourceManager.GetString("BillsFilter_" + f, Strings.Culture) ?? f.ToString())).ToList();
        SelectedFilter = Filters[0];
        Month = BudgetMonth.Of(Today);
        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        WeekdayNames = Enumerable.Range(0, 7).Select(i => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)(((int)first + i) % 7))).ToList();
        messenger.Register<LedgerChanged>(this);
        messenger.Register<RecurringChanged>(this);
        messenger.Register<AlertsChanged>(this);
    }

    /// <inheritdoc />
    public override string Title => Strings.Page_Bills_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Page_Bills_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => LastDetection is null ? Strings.Page_Bills_EmptyHeading : Strings.Bills_NothingHeading;

    /// <inheritdoc />
    public override string EmptyMessage => LastDetection is null ? Strings.Page_Bills_EmptyMessage : Strings.Bills_NothingMessage;

    /// <summary>Either empty state (before detection, or nothing found).</summary>
    public bool ShowAnyEmptyState => ShowEmptyState || ShowNoItems;

    /// <summary>The current load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Whether the first load finished.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowContent), nameof(ShowNoItems), nameof(ShowAnyEmptyState))]
    public partial bool IsInitialized { get; private set; }

    /// <summary>A detection run is in progress.</summary>
    [ObservableProperty]
    public partial bool IsDetecting { get; private set; }

    /// <summary>Load error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Whether loading failed.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Date of the last detection run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowContent), nameof(ShowNoItems), nameof(LastRunText), nameof(EmptyHeading), nameof(EmptyMessage), nameof(ShowAnyEmptyState))]
    public partial DateOnly? LastDetection { get; private set; }

    /// <summary>Whether any (non-dismissed or dismissed) item exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowContent), nameof(ShowNoItems), nameof(ShowAnyEmptyState))]
    public partial bool HasItems { get; private set; }

    /// <summary>The designed state before detection ever ran and before any item exists.</summary>
    public bool ShowEmptyState => IsInitialized && LastDetection is null && !HasItems;

    /// <summary>Detection ran but found nothing yet.</summary>
    public bool ShowNoItems => IsInitialized && LastDetection is not null && !HasItems;

    /// <summary>The tabs and totals.</summary>
    public bool ShowContent => IsInitialized && HasItems;

    /// <summary>"Last checked Sep 24, 2026".</summary>
    public string LastRunText => LastDetection is { } d ? LedgerText.Format(Strings.Bills_LastRun, BillsFormat.LongDate(d)) : Strings.Bills_NeverRun;

    /// <summary>Selected tab.</summary>
    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    /// <summary>Selected tab as an enum.</summary>
    public BillsTab SelectedTab
    {
        get => (BillsTab)SelectedTabIndex;
        set => SelectedTabIndex = (int)value;
    }

    // ---- Totals (F-REC-2)

    /// <summary>Monthly recurring outflow.</summary>
    [ObservableProperty]
    public partial string MonthlyOutflowText { get; private set; } = string.Empty;

    /// <summary>Monthly recurring inflow.</summary>
    [ObservableProperty]
    public partial string MonthlyInflowText { get; private set; } = string.Empty;

    /// <summary>Subscriptions per month.</summary>
    [ObservableProperty]
    public partial string SubscriptionsMonthlyText { get; private set; } = string.Empty;

    /// <summary>Subscriptions per year.</summary>
    [ObservableProperty]
    public partial string SubscriptionsYearlyText { get; private set; } = string.Empty;

    /// <summary>Bills per month.</summary>
    [ObservableProperty]
    public partial string BillsMonthlyText { get; private set; } = string.Empty;

    /// <summary>Items waiting for confirmation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetected), nameof(DetectedText))]
    public partial int DetectedCount { get; private set; }

    /// <summary>Whether detections wait for confirmation.</summary>
    public bool HasDetected => DetectedCount > 0;

    /// <summary>"3 detected items need your confirmation."</summary>
    public string DetectedText => LedgerText.Format(DetectedCount == 1 ? Strings.Bills_DetectedOne : Strings.Bills_DetectedMany, DetectedCount);

    // ---- List tab

    /// <summary>Filter choices.</summary>
    public IReadOnlyList<Choice<BillsFilter>> Filters { get; }

    /// <summary>Selected filter.</summary>
    [ObservableProperty]
    public partial Choice<BillsFilter> SelectedFilter { get; set; }

    /// <summary>Rows of the list for the filter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasListItems))]
    public partial IReadOnlyList<BillItemViewModel> ListItems { get; private set; } = [];

    /// <summary>Whether the filter matches anything.</summary>
    public bool HasListItems => ListItems.Count > 0;

    // ---- Calendar tab

    /// <summary>First day of the month shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthTitle))]
    public partial DateOnly Month { get; private set; }

    /// <summary>"September 2026".</summary>
    public string MonthTitle => Month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>Weekday headers in the culture's week order.</summary>
    public IReadOnlyList<string> WeekdayNames { get; }

    /// <summary>Six weeks of days.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<BillsCalendarDay> Days { get; private set; } = [];

    /// <summary>Net of the month's entries.</summary>
    [ObservableProperty]
    public partial string MonthTotalText { get; private set; } = string.Empty;

    // ---- Subscriptions tab

    /// <summary>Subscription rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubscriptions))]
    public partial IReadOnlyList<BillItemViewModel> Subscriptions { get; private set; } = [];

    /// <summary>Whether there are subscriptions.</summary>
    public bool HasSubscriptions => Subscriptions.Count > 0;

    /// <summary>Price changes of subscriptions, newest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPriceChanges))]
    public partial IReadOnlyList<PriceChangeRow> PriceChanges { get; private set; } = [];

    /// <summary>Whether any subscription changed price.</summary>
    public bool HasPriceChanges => PriceChanges.Count > 0;

    /// <summary>Category groups for the subscription designation.</summary>
    public ObservableCollection<SubscriptionGroupOption> SubscriptionGroups { get; } = [];

    /// <summary>"Subscriptions: Streaming" or "Choose groups".</summary>
    [ObservableProperty]
    public partial string SubscriptionGroupsLabel { get; private set; } = Strings.Bills_ChooseGroups;

    // ---- Detail panel

    /// <summary>The selected item's panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial BillDetailViewModel? Detail { get; private set; }

    /// <summary>Whether an item is selected.</summary>
    public bool HasDetail => Detail is not null;

    /// <summary>Raised after the detail changed (the view rebuilds its chart).</summary>
    public event EventHandler? DetailChanged;

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    /// <inheritdoc />
    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is Guid id)
        {
            _selectAfterLoad = id;
            SelectedTab = BillsTab.List;
        }

        Loading = LoadAsync();
    }

    /// <inheritdoc />
    public void Receive(LedgerChanged message) => Dispatcher.UIThread.Post(Refresh);

    /// <inheritdoc />
    public void Receive(RecurringChanged message) => Dispatcher.UIThread.Post(Refresh);

    /// <inheritdoc />
    public void Receive(AlertsChanged message) => Dispatcher.UIThread.Post(() =>
    {
        if (Detail is { } detail)
        {
            _ = SelectAsync(detail.Item.Id);
        }
    });

    /// <summary>Runs detection now (F-REC-1 on demand).</summary>
    [RelayCommand]
    public async Task RunDetectionAsync()
    {
        if (IsDetecting)
        {
            return;
        }

        IsDetecting = true;
        try
        {
            var summary = await _recurring.DetectAsync(Today, CancellationToken.None);
            _status.Show(BillsFormat.Summary(summary));
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException)
        {
            _status.Show(LedgerText.Format(Strings.Bills_DetectionFailed, ex.Message), isError: true);
        }
        finally
        {
            IsDetecting = false;
        }

        await (Loading = LoadAsync());
    }

    /// <summary>Adds an item by hand.</summary>
    [RelayCommand]
    public async Task AddItemAsync() => await EditItemCoreAsync(null);

    /// <summary>Edits the selected item.</summary>
    [RelayCommand]
    public async Task EditAsync()
    {
        if (Detail is { } detail)
        {
            await EditItemCoreAsync(detail.Item);
        }
    }

    /// <summary>Selects an item (list row, calendar entry, notification).</summary>
    [RelayCommand]
    public Task SelectItemAsync(BillItemViewModel? item) => item is null ? Task.CompletedTask : SelectAsync(item.Id);

    /// <summary>Opens what a calendar entry stands for.</summary>
    [RelayCommand]
    public async Task OpenEntryAsync(BillsCalendarEntry? entry)
    {
        if (entry?.ItemId is { } id)
        {
            await SelectAsync(id);
        }
        else if (entry?.ScheduledId is { } scheduledId)
        {
            await _schedules.ShowAsync(existingId: scheduledId);
        }
    }

    /// <summary>Closes the detail panel.</summary>
    [RelayCommand]
    public void CloseDetail()
    {
        Detail = null;
        foreach (var row in _all)
        {
            row.IsSelected = false;
        }

        DetailChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Confirms the selected detection.</summary>
    [RelayCommand]
    public Task ConfirmAsync() => ActAsync(id => _recurring.ConfirmAsync(id, CancellationToken.None), Strings.Bills_Confirmed);

    /// <summary>Pauses the selected item.</summary>
    [RelayCommand]
    public Task PauseAsync() => ActAsync(id => _recurring.PauseAsync(id, CancellationToken.None), Strings.Bills_Paused);

    /// <summary>Resumes the selected item.</summary>
    [RelayCommand]
    public Task ResumeAsync() => ActAsync(id => _recurring.ResumeAsync(id, CancellationToken.None), Strings.Bills_Resumed);

    /// <summary>Dismisses the selected item.</summary>
    [RelayCommand]
    public Task DismissAsync() => ActAsync(id => _recurring.DismissAsync(id, CancellationToken.None), Strings.Bills_Dismissed);

    /// <summary>Turns detection back on for the selected dismissed item's payee.</summary>
    [RelayCommand]
    public Task ReenableAsync() => ActAsync(_ => _recurring.ReenableDetectionAsync(Detail!.Item.PayeeId, CancellationToken.None), Strings.Bills_Reenabled, undoable: false);

    /// <summary>F-REC-4: a monthly set-aside target on the item's category.</summary>
    [RelayCommand]
    public async Task CreateTargetAsync()
    {
        if (Detail is not { CanCreateTarget: true } detail)
        {
            return;
        }

        try
        {
            await _recurring.CreateTargetAsync(detail.Item.Id, CancellationToken.None);
            _status.Show(LedgerText.Format(Strings.Bills_TargetCreated, detail.Item.CategoryName, detail.TargetText), offerUndo: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or LedgerValidationException)
        {
            _status.Show(ex.Message, isError: true);
        }
    }

    /// <summary>Creates a scheduled transaction pre-filled from the selected item and links it.</summary>
    [RelayCommand]
    public async Task CreateScheduleAsync()
    {
        if (Detail is not { } detail)
        {
            return;
        }

        var item = detail.Item;
        var rule = RecurringSchedule.InferRule(item.Cadence, item.NextExpectedDate, item.LastSeenDate);
        var dialog = await _schedules.ShowAsync(new ScheduledDraft(item.AccountId, item.PayeeName, item.ExpectedAmount.Amount, item.CategoryId, rule, item.Id));
        if (dialog is not null)
        {
            await SelectAsync(item.Id);
        }
    }

    /// <summary>Opens the item's transactions in the register (payee search).</summary>
    [RelayCommand]
    public void OpenTransactions()
    {
        if (Detail is { } detail)
        {
            _navigation.NavigateTo<AccountsViewModel>(new RegisterNavigation(detail.Item.AccountId, "payee:\"" + detail.Item.PayeeName + "\""));
        }
    }

    /// <summary>Dismisses one of the item's alerts.</summary>
    [RelayCommand]
    public async Task DismissAlertAsync(AlertItemViewModel? alert)
    {
        if (alert is not null)
        {
            await _alerts.DismissAsync(alert.Id, CancellationToken.None);
        }
    }

    /// <summary>Previous month in the calendar.</summary>
    [RelayCommand]
    public Task PreviousMonthAsync() => ChangeMonthAsync(Month.AddMonths(-1));

    /// <summary>Next month in the calendar.</summary>
    [RelayCommand]
    public Task NextMonthAsync() => ChangeMonthAsync(Month.AddMonths(1));

    /// <summary>Back to this month.</summary>
    [RelayCommand]
    public Task ThisMonthAsync() => ChangeMonthAsync(BudgetMonth.Of(Today));

    /// <summary>Selects an item by id and loads its detail.</summary>
    public async Task SelectAsync(Guid id)
    {
        foreach (var row in _all)
        {
            row.IsSelected = row.Id == id;
        }

        try
        {
            var detail = await _recurring.GetItemAsync(id, CancellationToken.None);
            if (detail is null)
            {
                CloseDetail();
                return;
            }

            var now = _time.GetUtcNow().UtcDateTime;
            var alerts = (await _alerts.GetAlertsAsync(false, CancellationToken.None))
                .Where(a => a.RecurringItemId == id)
                .Select(a => new AlertItemViewModel(a, now))
                .ToList();
            Detail = new BillDetailViewModel(detail, alerts, Today);
            DetailChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException)
        {
            ErrorMessage = LedgerText.Format(Strings.Bills_ErrorLoading, ex.Message);
        }
    }

    partial void OnSelectedFilterChanged(Choice<BillsFilter> value) => ApplyFilter();

    private void Refresh()
    {
        if (IsInitialized)
        {
            Loading = LoadAsync();
        }
    }

    private async Task ActAsync(Func<Guid, Task> action, string done, bool undoable = true)
    {
        if (Detail is not { } detail)
        {
            return;
        }

        try
        {
            await action(detail.Item.Id);
            _status.Show(LedgerText.Format(done, detail.Item.PayeeName), offerUndo: undoable);
        }
        catch (Exception ex) when (ex is InvalidOperationException or LedgerValidationException)
        {
            _status.Show(ex.Message, isError: true);
        }

        await (Loading = LoadAsync());
    }

    private async Task EditItemCoreAsync(RecurringItemDto? existing)
    {
        var accounts = await _accounts.GetAccountsAsync(includeClosed: false, CancellationToken.None);
        var categories = await _categories.GetCategoriesAsync(includeHidden: false, CancellationToken.None);
        var dialog = new RecurringItemEditorViewModel(
            _recurring,
            _payees,
            accounts.Select(AccountOption.From).ToList(),
            categories.Where(c => !c.IsCreditCardPayment).Select(CategoryOption.From).ToList(),
            Today,
            existing);
        if (await _dialogs.ShowAsync(dialog) && dialog.Result is { } saved)
        {
            _status.Show(LedgerText.Format(existing is null ? Strings.Bills_ItemAdded : Strings.Bills_ItemSaved, saved.PayeeName), offerUndo: true);
            _selectAfterLoad = saved.Id;
            await (Loading = LoadAsync());
        }
    }

    private async Task ChangeMonthAsync(DateOnly month)
    {
        Month = month;
        try
        {
            await LoadCalendarAsync(_version);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException)
        {
            ErrorMessage = LedgerText.Format(Strings.Bills_ErrorLoading, ex.Message);
        }
    }

    private async Task LoadAsync()
    {
        var version = ++_version;
        try
        {
            var today = Today;
            var itemsTask = _recurring.GetItemsAsync(new RecurringItemFilter([.. Enum.GetValues<RecurringStatus>()]), CancellationToken.None);
            var totalsTask = _recurring.GetTotalsAsync(includeUnconfirmed: true, CancellationToken.None);
            var lastTask = _recurring.GetLastDetectionDateAsync(CancellationToken.None);
            var items = await itemsTask;
            var totals = await totalsTask;
            var last = await lastTask;
            if (version != _version)
            {
                return;
            }

            ErrorMessage = null;
            LastDetection = last;
            var selected = Detail?.Item.Id;
            _all = items.Select(i => new BillItemViewModel(i, today)).ToList();
            HasItems = _all.Count > 0;
            DetectedCount = _all.Count(i => i.IsDetected);
            MonthlyOutflowText = LedgerText.Money(Math.Abs(totals.MonthlyOutflow.Amount), totals.MonthlyOutflow.Currency);
            MonthlyInflowText = LedgerText.Money(totals.MonthlyInflow.Amount, totals.MonthlyInflow.Currency);
            SubscriptionsMonthlyText = LedgerText.Money(Math.Abs(totals.SubscriptionsMonthly.Amount), totals.SubscriptionsMonthly.Currency);
            SubscriptionsYearlyText = LedgerText.Money(Math.Abs(totals.SubscriptionsYearly.Amount), totals.SubscriptionsYearly.Currency);
            BillsMonthlyText = LedgerText.Money(Math.Abs(totals.BillsMonthly.Amount), totals.BillsMonthly.Currency);
            ApplyFilter();
            Subscriptions = _all.Where(i => i.IsSubscription && !i.IsIncome && i.Status != RecurringStatus.Dismissed).OrderBy(i => i.PayeeName, StringComparer.CurrentCultureIgnoreCase).ToList();
            await LoadSubscriptionExtrasAsync(version);
            await LoadCalendarAsync(version);

            var target = _selectAfterLoad ?? selected;
            _selectAfterLoad = null;
            if (target is { } id && _all.Any(i => i.Id == id))
            {
                if (_all.First(i => i.Id == id) is { } row && !ListItems.Contains(row))
                {
                    SelectedFilter = Filters[0];
                    if (!ListItems.Contains(row))
                    {
                        SelectedFilter = Filters.First(f => Matches(f.Value, row.Status));
                    }
                }

                await SelectAsync(id);
            }
            else if (Detail is not null)
            {
                CloseDetail();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException)
        {
            ErrorMessage = LedgerText.Format(Strings.Bills_ErrorLoading, ex.Message);
        }
        finally
        {
            if (version == _version)
            {
                IsInitialized = true;
            }
        }
    }

    private static bool Matches(BillsFilter filter, RecurringStatus status) => filter switch
    {
        BillsFilter.Active => status == RecurringStatus.Active,
        BillsFilter.Detected => status == RecurringStatus.Detected,
        BillsFilter.Paused => status is RecurringStatus.Paused or RecurringStatus.Ended,
        BillsFilter.Dismissed => status == RecurringStatus.Dismissed,
        _ => status != RecurringStatus.Dismissed,
    };

    private void ApplyFilter()
    {
        var filter = SelectedFilter?.Value ?? BillsFilter.All;
        ListItems = _all.Where(i => Matches(filter, i.Status)).ToList();
    }

    private async Task LoadSubscriptionExtrasAsync(int version)
    {
        var groups = (await _categories.GetCategoriesAsync(includeHidden: false, CancellationToken.None))
            .Where(c => !c.IsSystem && !c.IsCreditCardPayment)
            .GroupBy(c => (c.GroupId, c.GroupName))
            .Select(g => g.Key)
            .ToList();
        var designations = await _recurring.GetSubscriptionDesignationsAsync(CancellationToken.None);
        var changes = new List<PriceChangeRow>();
        foreach (var subscription in Subscriptions)
        {
            if (await _recurring.GetItemAsync(subscription.Id, CancellationToken.None) is { } detail)
            {
                changes.AddRange(BillDetailViewModel.PriceChangesOf(detail));
            }
        }

        if (version != _version)
        {
            return;
        }

        PriceChanges = changes.OrderByDescending(c => c.Date).ToList();
        _suppressDesignations = true;
        SubscriptionGroups.Clear();
        foreach (var (id, name) in groups)
        {
            SubscriptionGroups.Add(new SubscriptionGroupOption(id, name, DesignationsChanged) { IsChecked = designations.GroupIds.Contains(id) });
        }

        _suppressDesignations = false;
        UpdateGroupsLabel();
    }

    private void UpdateGroupsLabel()
    {
        var chosen = SubscriptionGroups.Where(g => g.IsChecked).Select(g => g.Name).ToList();
        SubscriptionGroupsLabel = chosen.Count == 0 ? Strings.Bills_ChooseGroups : LedgerText.Format(Strings.Bills_GroupsChosen, string.Join(", ", chosen));
    }

    private void DesignationsChanged()
    {
        if (_suppressDesignations)
        {
            return;
        }

        UpdateGroupsLabel();
        var groups = SubscriptionGroups.Where(g => g.IsChecked).Select(g => g.Id).ToList();
        _ = SaveDesignationsAsync(groups);
    }

    private async Task SaveDesignationsAsync(IReadOnlyCollection<Guid> groups)
    {
        try
        {
            var current = await _recurring.GetSubscriptionDesignationsAsync(CancellationToken.None);
            await _recurring.SetSubscriptionDesignationsAsync(new SubscriptionDesignations(groups, current.TagIds), CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            _status.Show(ex.Message, isError: true);
        }
    }

    private async Task LoadCalendarAsync(int version)
    {
        var today = Today;
        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var offset = ((int)Month.DayOfWeek - (int)first + 7) % 7;
        var gridStart = Month.AddDays(-offset);
        var gridEnd = gridStart.AddDays(41);
        var occurrences = await _recurring.GetOccurrencesAsync(gridStart, gridEnd, CancellationToken.None);
        var scheduled = await _scheduled.GetUpcomingAsync(gridStart, gridEnd, null, CancellationToken.None);
        if (version != _version)
        {
            return;
        }

        var names = _all.ToDictionary(i => i.Id, i => i.PayeeName);
        var linked = _all.Select(i => i.Item.ScheduledTransactionId).OfType<Guid>().ToHashSet();
        var currency = _all.FirstOrDefault()?.Item.ExpectedAmount.Currency ?? Currency.Default;
        var entries = occurrences
            .Where(o => names.ContainsKey(o.ItemId))
            .Select(o => (o.Date, Entry: new BillsCalendarEntry(o.ItemId, null, names[o.ItemId], o.Amount.Amount, o.Amount.Currency, !o.IsExpected, OpenEntryCommand)))
            .Concat(scheduled
                .Where(s => !linked.Contains(s.ScheduledId))
                .Select(s => (s.Date, Entry: new BillsCalendarEntry(null, s.ScheduledId, s.PayeeName, s.Amount.Amount, s.Amount.Currency, false, OpenEntryCommand))))
            .ToLookup(e => e.Date, e => e.Entry);
        Days = Enumerable.Range(0, 42).Select(i =>
        {
            var date = gridStart.AddDays(i);
            var list = entries[date].OrderBy(e => e.Amount > 0).ThenBy(e => e.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
            return new BillsCalendarDay(date, date.Month == Month.Month && date.Year == Month.Year, date == today, list, currency);
        }).ToList();
        var monthNet = Days.Where(d => d.IsCurrentMonth).SelectMany(d => d.Entries).Sum(e => e.Amount);
        MonthTotalText = LedgerText.Format(Strings.Bills_MonthNet, LedgerText.Money(monthNet, currency));
    }
}
