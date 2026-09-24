using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Accounts;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Navigation;
using Keel.Application.Undo;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Import;
using Keel.Domain;

namespace Keel.Desktop.ViewModels;

/// <summary>
/// The shell (PRD 9.1): sidebar navigation with the Accounts section, top bar (file name, search,
/// sync, alerts, undo/redo), content area, dialog layer, and status strip with the undo toast.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase, IRecipient<LedgerChanged>
{
    /// <summary>Narrowest expanded sidebar.</summary>
    public const double MinSidebarWidth = 180;

    /// <summary>Widest expanded sidebar.</summary>
    public const double MaxSidebarWidth = 400;

    private readonly INavigationService _navigation;
    private readonly IAccountService _accounts;
    private readonly IUndoService _undo;
    private readonly ImportWorkflow _import;

    /// <summary>Creates the shell, starts loading the Accounts section, and navigates to Home.</summary>
    public ShellViewModel(
        INavigationService navigation,
        AppSession session,
        PlatformShortcuts shortcuts,
        IAccountService accounts,
        IUndoService undo,
        DialogService dialogs,
        StatusService status,
        IMessenger messenger,
        ImportWorkflow import,
        Alerts.NotificationCenterViewModel? notifications = null,
        RecurringJobs? jobs = null)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        _import = import;
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(shortcuts);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(messenger);
        _navigation = navigation;
        _accounts = accounts;
        _undo = undo;
        Shortcuts = shortcuts;
        Dialogs = dialogs;
        Status = status;
        Status.PropertyChanged += OnStatusChanged;
        _undo.Changed += (_, _) => Dispatcher.UIThread.Post(OnUndoChanged);
        messenger.Register(this);

        PrimaryItems =
        [
            Item<HomeViewModel>(Strings.Nav_Home, "Icon.Home"),
            Item<BudgetViewModel>(Strings.Nav_Budget, "Icon.Budget"),
            Item<ReviewViewModel>(Strings.Nav_Review, "Icon.Review"),
            Item<BillsViewModel>(Strings.Nav_Bills, "Icon.Bills"),
            Item<GoalsViewModel>(Strings.Nav_Goals, "Icon.Goals"),
            Item<ReportsViewModel>(Strings.Nav_Reports, "Icon.Reports"),
        ];
        AccountItems = [Item<AccountsViewModel>(Strings.Nav_AllAccounts, "Icon.Accounts")];
        SettingsItem = Item<SettingsViewModel>(Strings.Nav_Settings, "Icon.Settings");

        SidebarWidth = WindowPlacementService.DefaultSidebarWidth;
        FileName = session.BudgetFile?.FileName ?? Strings.Shell_NoFile;
        FilePath = session.BudgetFile?.Path ?? string.Empty;
        Status.Show(session.StartupMessage ?? string.Empty);
        SearchWatermark = string.Format(CultureInfo.CurrentCulture, Strings.Shell_SearchWatermark, shortcuts.Format(shortcuts.Search));
        ToggleSidebarToolTip = $"{Strings.Shell_ToggleSidebar} ({shortcuts.Format(shortcuts.ToggleSidebar)})";

        _navigation.Navigated += OnNavigated;
        _navigation.NavigateTo<HomeViewModel>();
        AccountsLoading = session.BudgetFile is null ? Task.CompletedTask : RefreshAccountsAsync();
        Notifications = notifications;
        Jobs = jobs;
        jobs?.Start();
    }

    /// <summary>The notification center behind the top-bar bell (M5).</summary>
    public Alerts.NotificationCenterViewModel? Notifications { get; }

    /// <summary>Scheduled entry and recurring detection jobs (M5; tests await <see cref="RecurringJobs.Running"/>).</summary>
    public RecurringJobs? Jobs { get; }

    /// <summary>In-window dialogs.</summary>
    public DialogService Dialogs { get; }

    /// <summary>Status strip and undo toast.</summary>
    public StatusService Status { get; }

    /// <summary>The sidebar account groups (Cash, Credit, Tracking, and Closed when shown).</summary>
    public ObservableCollection<SidebarAccountGroupViewModel> AccountGroups { get; } = [];

    /// <summary>Whether any open account exists.</summary>
    [ObservableProperty]
    public partial bool HasAccounts { get; private set; }

    /// <summary>Whether closed accounts exist (shows the "show closed" toggle).</summary>
    [ObservableProperty]
    public partial bool HasClosedAccounts { get; private set; }

    /// <summary>Whether closed accounts are listed.</summary>
    [ObservableProperty]
    public partial bool ShowClosedAccounts { get; set; }

    /// <summary>The latest sidebar refresh (tests await it).</summary>
    public Task AccountsLoading { get; private set; }

    /// <summary>Undo button tooltip: the next action and the platform shortcut.</summary>
    public string UndoToolTip => _undo.NextUndo is { } action
        ? string.Format(CultureInfo.CurrentCulture, Strings.Shell_UndoActionTip, LedgerText.Action(action), Shortcuts.Format(Shortcuts.Undo))
        : string.Format(CultureInfo.CurrentCulture, Strings.Shell_UndoTip, Shortcuts.Format(Shortcuts.Undo));

    /// <summary>Redo button tooltip: the next action and the platform shortcut.</summary>
    public string RedoToolTip => _undo.NextRedo is { } action
        ? string.Format(CultureInfo.CurrentCulture, Strings.Shell_RedoActionTip, LedgerText.Action(action), Shortcuts.Format(Shortcuts.Redo))
        : string.Format(CultureInfo.CurrentCulture, Strings.Shell_RedoTip, Shortcuts.Format(Shortcuts.Redo));

    /// <summary>Message in the status strip.</summary>
    public string StatusMessage
    {
        get => Status.Message;
        set => Status.Show(value);
    }

    /// <inheritdoc />
    public void Receive(LedgerChanged message) =>
        Dispatcher.UIThread.Post(() => AccountsLoading = RefreshAccountsAsync());

    /// <summary>Reloads the Accounts section of the sidebar.</summary>
    public async Task RefreshAccountsAsync()
    {
        IReadOnlyList<AccountDto> accounts;
        try
        {
            accounts = await _accounts.GetAccountsAsync(includeClosed: true, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            Status.Show(ex.Message, isError: true);
            return;
        }

        var open = accounts.Where(a => !a.IsClosed).ToList();
        var groups = Enum.GetValues<AccountGroup>()
            .Select(g => (Group: (AccountGroup?)g, Title: LedgerText.Group(g), Items: open.Where(a => a.Group == g).ToList()))
            .ToList();
        var closed = accounts.Where(a => a.IsClosed).ToList();
        if (ShowClosedAccounts && closed.Count > 0)
        {
            groups.Add((null, Strings.Nav_ClosedAccounts, closed));
        }

        groups = groups.Where(g => g.Items.Count > 0).ToList();
        var sameShape = AccountGroups.Count == groups.Count
            && AccountGroups.Zip(groups).All(p => p.First.Title == p.Second.Title
                && p.First.Accounts.Select(a => a.Id).SequenceEqual(p.Second.Items.Select(a => a.Id)));
        if (sameShape)
        {
            // Incremental refresh: same accounts in the same places, so update balances and names in place.
            foreach (var (vm, (_, _, items)) in AccountGroups.Zip(groups))
            {
                for (var i = 0; i < items.Count; i++)
                {
                    vm.Accounts[i].Account = items[i];
                }

                var currency = items[0].Balance.Currency;
                vm.TotalText = LedgerText.Money(items.Where(a => a.Balance.Currency == currency).Sum(a => a.Balance.Amount), currency);
            }

            HasAccounts = open.Count > 0;
            HasClosedAccounts = closed.Count > 0;
            return;
        }

        AccountGroups.Clear();
        foreach (var (group, title, items) in groups)
        {
            var vm = new SidebarAccountGroupViewModel(group, title);
            foreach (var account in items)
            {
                vm.Accounts.Add(new SidebarAccountViewModel(this, account) { IsSelected = IsCurrentAccount(account.Id) });
            }

            var currency = items[0].Balance.Currency;
            vm.TotalText = LedgerText.Money(items.Where(a => a.Balance.Currency == currency).Sum(a => a.Balance.Amount), currency);
            AccountGroups.Add(vm);
        }

        HasAccounts = open.Count > 0;
        HasClosedAccounts = closed.Count > 0;
    }

    /// <summary>Opens the register of an account.</summary>
    public void OpenAccount(Guid accountId) => _navigation.NavigateTo<AccountsViewModel>(accountId);

    /// <summary>Opens the edit-account dialog.</summary>
    public async Task EditAccountAsync(AccountDto account)
    {
        var dialog = new AccountEditorViewModel(_accounts, account);
        if (await Dialogs.ShowAsync(dialog))
        {
            Status.Show(LedgerText.Format(Strings.Status_AccountSaved, dialog.Result?.Name ?? account.Name), offerUndo: true);
        }
    }

    /// <summary>Imports a bank file into an account (sidebar account menu), showing its register.</summary>
    public Task ImportFileAsync(Guid accountId)
    {
        OpenAccount(accountId);
        return _import.ImportAsync(accountId);
    }

    /// <summary>Moves an account up or down within its sidebar group (F-ACC-1 reorder).</summary>
    public async Task MoveAccountAsync(SidebarAccountViewModel item, int delta)
    {
        ArgumentNullException.ThrowIfNull(item);
        var group = AccountGroups.FirstOrDefault(g => g.Accounts.Contains(item));
        if (group?.Group is not { } accountGroup)
        {
            return;
        }

        var order = group.Accounts.Select(a => a.Id).ToList();
        var index = order.IndexOf(item.Id);
        var target = index + delta;
        if (target < 0 || target >= order.Count)
        {
            return;
        }

        (order[index], order[target]) = (order[target], order[index]);
        await _accounts.ReorderAccountsAsync(accountGroup, order, CancellationToken.None);
    }

    /// <summary>Platform shortcuts (for key bindings in the window).</summary>
    public PlatformShortcuts Shortcuts { get; }

    /// <summary>Home, Budget, Review, Bills, Goals, Reports.</summary>
    public IReadOnlyList<NavigationItemViewModel> PrimaryItems { get; }

    /// <summary>The Accounts section.</summary>
    public IReadOnlyList<NavigationItemViewModel> AccountItems { get; }

    /// <summary>Settings, pinned to the bottom of the sidebar.</summary>
    public NavigationItemViewModel SettingsItem { get; }

    /// <summary>Every navigation entry in sidebar order.</summary>
    public IEnumerable<NavigationItemViewModel> AllItems => PrimaryItems.Concat(AccountItems).Append(SettingsItem);

    /// <summary>The page shown in the content area.</summary>
    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; private set; }

    /// <summary>Whether the sidebar shows icons only.</summary>
    [ObservableProperty]
    public partial bool IsSidebarCollapsed { get; set; }

    /// <summary>Expanded sidebar width.</summary>
    [ObservableProperty]
    public partial double SidebarWidth { get; set; }

    /// <summary>Text in the global search box.</summary>
    [ObservableProperty]
    public partial string? SearchText { get; set; }

    /// <summary>Open budget file name.</summary>
    public string FileName { get; }

    /// <summary>Open budget file path (tooltip).</summary>
    public string FilePath { get; }

    /// <summary>Window title.</summary>
    public string WindowTitle => $"{FileName} - {Strings.App_Name}";

    /// <summary>Search box placeholder including the platform shortcut.</summary>
    public string SearchWatermark { get; }

    /// <summary>Sidebar toggle tooltip including the platform shortcut.</summary>
    public string ToggleSidebarToolTip { get; }

    /// <summary>Label of the sync button.</summary>
    public string SyncAllLabel => Strings.Shell_SyncAll;

    /// <summary>Sync button tooltip.</summary>
    public string SyncToolTip => Strings.Shell_SyncUnavailable;

    /// <summary>Alerts button tooltip.</summary>
    public string AlertsToolTip => Strings.Shell_Alerts;

    /// <summary>Right-hand status-strip text.</summary>
    public string LocalOnlyText => Strings.Shell_LocalOnly;

    /// <summary>Clamps and applies a new expanded sidebar width (from the splitter).</summary>
    public void ResizeSidebar(double width) => SidebarWidth = Math.Clamp(width, MinSidebarWidth, MaxSidebarWidth);

    /// <summary>Navigates to a page type registered in DI.</summary>
    public void NavigateTo<TViewModel>()
        where TViewModel : class => _navigation.NavigateTo<TViewModel>();

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        try
        {
            if (await _undo.UndoAsync(CancellationToken.None) is { } action)
            {
                Status.Show(string.Format(CultureInfo.CurrentCulture, Strings.Status_Undone, LedgerText.Action(action)));
            }
        }
        catch (LedgerValidationException ex)
        {
            Status.Show(LedgerText.Error(ex.Error), isError: true);
        }
        catch (InvalidOperationException ex)
        {
            Status.Show(string.Format(CultureInfo.CurrentCulture, Strings.Status_UndoFailed, ex.Message), isError: true);
        }
    }

    private bool CanUndo() => _undo.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync()
    {
        try
        {
            if (await _undo.RedoAsync(CancellationToken.None) is { } action)
            {
                Status.Show(string.Format(CultureInfo.CurrentCulture, Strings.Status_Redone, LedgerText.Action(action)));
            }
        }
        catch (InvalidOperationException ex)
        {
            Status.Show(string.Format(CultureInfo.CurrentCulture, Strings.Status_UndoFailed, ex.Message), isError: true);
        }
    }

    private bool CanRedo() => _undo.CanRedo;

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        var dialog = new AccountEditorViewModel(_accounts);
        if (await Dialogs.ShowAsync(dialog) && dialog.Result is { } created)
        {
            Status.Show(string.Format(CultureInfo.CurrentCulture, Strings.Status_AccountAdded, created.Name), offerUndo: true);
            OpenAccount(created.Id);
        }
    }

    [RelayCommand]
    private void Search()
    {
        _navigation.NavigateTo<AccountsViewModel>(new RegisterNavigation(null, string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim()));
    }

    partial void OnShowClosedAccountsChanged(bool value) => AccountsLoading = RefreshAccountsAsync();

    private void OnUndoChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
    }

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatusService.Message))
        {
            OnPropertyChanged(nameof(StatusMessage));
        }
    }

    private bool IsCurrentAccount(Guid id) => CurrentPage is AccountsViewModel { AccountId: { } current } && current == id;

    // Sync needs a bank connection (M7); with none configured the command is disabled.
    [RelayCommand(CanExecute = nameof(CanSyncAll))]
    private void SyncAll()
    {
    }

    private static bool CanSyncAll() => false;

    private NavigationItemViewModel Item<TViewModel>(string title, string iconKey)
        where TViewModel : class => new(title, iconKey, typeof(TViewModel), () => _navigation.NavigateTo<TViewModel>());

    private void OnNavigated(object? sender, NavigatedEventArgs e)
    {
        CurrentPage = e.ViewModel as ViewModelBase;
        var accountPage = e.ViewModel is AccountsViewModel { AccountId: not null };
        foreach (var item in AllItems)
        {
            item.IsSelected = item.PageType == e.ViewModel.GetType() && !accountPage;
        }

        foreach (var account in AccountGroups.SelectMany(g => g.Accounts))
        {
            account.IsSelected = IsCurrentAccount(account.Id);
        }
    }
}
