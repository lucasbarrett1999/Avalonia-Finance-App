using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Navigation;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels;

/// <summary>
/// The shell (PRD 9.1): sidebar navigation, top bar (file name, search, sync, alerts,
/// undo/redo), content area, and status strip.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    /// <summary>Narrowest expanded sidebar.</summary>
    public const double MinSidebarWidth = 180;

    /// <summary>Widest expanded sidebar.</summary>
    public const double MaxSidebarWidth = 400;

    private readonly INavigationService _navigation;

    /// <summary>Creates the shell and navigates to Home.</summary>
    public ShellViewModel(INavigationService navigation, AppSession session, PlatformShortcuts shortcuts)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(shortcuts);
        _navigation = navigation;
        Shortcuts = shortcuts;

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
        StatusMessage = session.StartupMessage ?? string.Empty;
        SearchWatermark = string.Format(CultureInfo.CurrentCulture, Strings.Shell_SearchWatermark, shortcuts.Format(shortcuts.Search));
        UndoToolTip = string.Format(CultureInfo.CurrentCulture, Strings.Shell_UndoTip, shortcuts.Format(shortcuts.Undo));
        RedoToolTip = string.Format(CultureInfo.CurrentCulture, Strings.Shell_RedoTip, shortcuts.Format(shortcuts.Redo));
        ToggleSidebarToolTip = $"{Strings.Shell_ToggleSidebar} ({shortcuts.Format(shortcuts.ToggleSidebar)})";

        _navigation.Navigated += OnNavigated;
        _navigation.NavigateTo<HomeViewModel>();
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

    /// <summary>Message in the status strip.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    /// <summary>Open budget file name.</summary>
    public string FileName { get; }

    /// <summary>Open budget file path (tooltip).</summary>
    public string FilePath { get; }

    /// <summary>Window title.</summary>
    public string WindowTitle => $"{FileName} - {Strings.App_Name}";

    /// <summary>Search box placeholder including the platform shortcut.</summary>
    public string SearchWatermark { get; }

    /// <summary>Undo button tooltip including the platform shortcut.</summary>
    public string UndoToolTip { get; }

    /// <summary>Redo button tooltip including the platform shortcut.</summary>
    public string RedoToolTip { get; }

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

    // Undo/redo arrive with audit events in M1; until then there is nothing to undo.
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
    }

    private static bool CanUndo() => false;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
    }

    private static bool CanRedo() => false;

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
        foreach (var item in AllItems)
        {
            item.IsSelected = item.PageType == e.ViewModel.GetType();
        }
    }
}
