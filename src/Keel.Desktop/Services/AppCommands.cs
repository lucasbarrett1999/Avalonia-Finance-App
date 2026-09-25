using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Keel.Application.Settings;
using Keel.Desktop.Resources;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Bills;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Reports;
using Keel.Desktop.ViewModels.Rules;
using Keel.Desktop.ViewModels.Settings;
using Keel.Desktop.ViewModels.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.Services;

/// <summary>An action or navigation target of the command palette and the menus.</summary>
/// <param name="Id">Stable id; shortcuts come from the <see cref="ShortcutRegistry"/> entry with the same id.</param>
/// <param name="Title">Text shown and searched.</param>
/// <param name="Section">Group label ("Go to", "File", …).</param>
/// <param name="Execute">What it does.</param>
/// <param name="Keys">Shortcut text, if any.</param>
/// <param name="Gesture">Shortcut gesture, if any (menus show it).</param>
public sealed record AppCommand(string Id, string Title, string Section, Action Execute, string? Keys = null, KeyGesture? Gesture = null)
{
    /// <summary>
    /// Whether the action can run now (F-SET-5, ADR 0103): evaluated when the palette opens, from the open file and
    /// the current screen. The palette lists unavailable actions dimmed and does not run them.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Screen-reader text: title, section and shortcut (and "unavailable").</summary>
    public string AutomationName
    {
        get
        {
            var name = Keys is null ? $"{Title}, {Section}" : $"{Title}, {Section}, {Keys}";
            return IsEnabled ? name : LedgerText.Format(Strings.Palette_UnavailableName, name);
        }
    }
}

/// <summary>A menu entry: a command, a submenu, or a separator.</summary>
/// <param name="Header">Label (with an access-key underscore for in-window menus).</param>
/// <param name="Command">Command for a leaf.</param>
/// <param name="Children">Items of a submenu.</param>
public sealed record MenuNode(string Header, AppCommand? Command = null, IReadOnlyList<MenuNode>? Children = null)
{
    /// <summary>A separator line.</summary>
    public static MenuNode Separator { get; } = new("-");

    /// <summary>Whether this is a separator.</summary>
    public bool IsSeparator => Header == "-" && Command is null && Children is null;
}

/// <summary>
/// Every user-facing action in one place (F-SET-5, PRD 9.1): the command palette lists these commands
/// and the menus (the macOS native menu, the in-window menu bar elsewhere, PRD 8) are built from them,
/// with shortcuts from the <see cref="ShortcutRegistry"/>.
/// </summary>
public sealed class AppCommands(IServiceProvider services, ShortcutRegistry registry, ThemeService themes)
{
    /// <summary>The shortcut registry.</summary>
    public ShortcutRegistry Registry => registry;

    /// <summary>The palette's commands for the current state of <paramref name="shell"/>.</summary>
    public IReadOnlyList<AppCommand> Build(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var go = Strings.Palette_SectionGoTo;
        var file = Strings.Palette_SectionFile;
        var edit = Strings.Palette_SectionEdit;
        var view = Strings.Palette_SectionView;
        var actions = Strings.Palette_SectionActions;
        var help = Strings.Palette_SectionHelp;
        var list = new List<AppCommand>();

        var pages = shell.PrimaryItems.Concat(shell.AccountItems).ToList();
        for (var i = 0; i < pages.Count; i++)
        {
            var item = pages[i];
            list.Add(Cmd("go-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), item.Title, go, () => item.NavigateCommand.Execute(null)));
        }

        foreach (var account in shell.AccountGroups.SelectMany(g => g.Accounts))
        {
            var id = account.Id;
            list.Add(Cmd("account-" + id.ToString("N"), LedgerText.Format(Strings.Palette_Account, account.Name), go, () => shell.OpenAccount(id)));
        }

        list.Add(Cmd("settings", Strings.Nav_Settings, go, () => shell.NavigateToSettings(null)));
        foreach (var (section, title) in SettingsSections)
        {
            list.Add(Cmd("settings-" + section, LedgerText.Format(Strings.Palette_SettingsSection, title), go, () => shell.NavigateToSettings(section)));
        }

        list.Add(Cmd("new-file", Strings.Menu_NewFile, file, () => _ = DataFile().NewFileAsync()));
        list.Add(Cmd("open-file", Strings.Menu_OpenFile, file, () => _ = DataFile().OpenFileAsync()));
        list.Add(Cmd("move-file", Strings.Menu_ChangeLocation, file, () => _ = DataFile().ChangeLocationAsync()));
        list.Add(Cmd("import", Strings.Menu_ImportFile, file, () => _ = shell.ImportIntoCurrentOrChosenAccountAsync()));
        list.Add(Cmd("backup", Strings.Menu_BackupNow, file, () => _ = DataFile().BackupNowAsync()));
        list.Add(Cmd("restore", Strings.Menu_Restore, file, () => shell.NavigateToSettings("General")));
        list.Add(Cmd("integrity", Strings.Menu_CheckIntegrity, file, () => _ = DataFile().CheckIntegrityAsync()));
        list.Add(Cmd("diagnostics", Strings.Menu_DiagnosticBundle, help, () => _ = DataFile().CopyDiagnosticBundleAsync()));

        list.Add(Cmd("undo", Strings.Shell_Undo, edit, () => Run(shell.UndoCommand)));
        list.Add(Cmd("redo", Strings.Shell_Redo, edit, () => Run(shell.RedoCommand)));
        list.Add(Cmd("search", Strings.Menu_Find, edit, shell.RequestSearchFocus));

        list.Add(Cmd("sidebar", Strings.Shell_ToggleSidebar, view, () => shell.ToggleSidebarCommand.Execute(null)));
        list.Add(Cmd("theme-system", Strings.Palette_ThemeSystem, view, () => themes.SetTheme(AppTheme.System)));
        list.Add(Cmd("theme-light", Strings.Palette_ThemeLight, view, () => themes.SetTheme(AppTheme.Light)));
        list.Add(Cmd("theme-dark", Strings.Palette_ThemeDark, view, () => themes.SetTheme(AppTheme.Dark)));
        list.Add(Cmd("density-comfortable", Strings.Palette_DensityComfortable, view, () => Appearance().SetDensity(UiDensity.Comfortable)));
        list.Add(Cmd("density-compact", Strings.Palette_DensityCompact, view, () => Appearance().SetDensity(UiDensity.Compact)));

        list.Add(Cmd("add-account", Strings.Nav_AddAccount, actions, () => Run(shell.AddAccountCommand)));
        list.Add(Cmd("add-transaction", Strings.Palette_AddTransaction, actions, () => _ = shell.AddTransactionAsync()));
        list.Add(Cmd("start-review", Strings.Palette_StartReview, actions, () => shell.NavigateTo<ReviewViewModel>()));
        list.Add(Cmd("sync-all", Strings.Shortcut_SyncAll, actions, () => Run(shell.SyncAllCommand)));
        list.Add(Cmd("budget-fund", Strings.Shortcut_BudgetFundTargets, actions, () => OnPage<BudgetViewModel>(shell, b => _ = b.FundTargetsAsync())));
        list.Add(Cmd("budget-move", Strings.Shortcut_BudgetMoveMoney, actions, () => OnPage<BudgetViewModel>(shell, b => _ = b.MoveMoneyAsync())));
        list.Add(Cmd("bills-detect", Strings.Shortcut_BillsDetect, actions, () => OnPage<BillsViewModel>(shell, b => _ = b.RunDetectionAsync())));
        list.Add(Cmd("bills-new", Strings.Shortcut_BillsNew, actions, () => OnPage<BillsViewModel>(shell, b => _ = b.AddItemAsync())));
        list.Add(Cmd("goals-new", Strings.Shortcut_GoalsNew, actions, () => OnPage<GoalsViewModel>(shell, g => _ = g.NewGoalAsync())));
        list.Add(Cmd("reports-export", Strings.Shortcut_ReportsExport, actions, () => OnPage<ReportsViewModel>(shell, r => _ = r.ExportCsvAsync())));
        list.Add(Cmd("manage-tags", Strings.Tag_PaletteManage, actions, () => shell.NavigateToSettings("Tags")));
        list.Add(Cmd("merge-payees", Strings.PayeeMerge_Palette, actions, () => shell.NavigateToSettings("Payees")));
        if (shell.CurrentPage is AccountsViewModel register && register.Selection.Count == 1)
        {
            list.Add(Cmd("attach-file", Strings.Attachment_PaletteAttach, actions, () => _ = register.AttachToSelectedAsync()));
            list.Add(Cmd("register-tags", Strings.Tag_PaletteEdit, actions, () => _ = register.EditTagsAsync()));
        }
        list.Add(Cmd("budget-three-months", Strings.BudgetMonths_Shortcut, view, () => OnPage<BudgetViewModel>(shell, b => b.ToggleThreeMonths())));
        list.Add(Cmd("budget-flex", Strings.Flex_Shortcut, view, () => OnPage<BudgetViewModel>(shell, b => b.ToggleFlexView())));

        list.Add(Cmd("shortcuts", Strings.Menu_KeyboardShortcuts, help, () => shell.NavigateToSettings("Keyboard")));
        list.Add(Cmd("guide", Strings.Menu_UserGuide, help, () => _ = services.GetRequiredService<IBrowserLauncher>().OpenAsync(KeelInfo.UserGuide)));
        list.Add(Cmd("about", Strings.Menu_About, help, () => _ = shell.Dialogs.ShowAsync(new AboutDialogViewModel(services.GetRequiredService<IBrowserLauncher>()))));
        if (registry.Find("quit") is not null)
        {
            list.Add(Cmd("quit", Strings.Shortcut_Quit, file, Quit));
        }

        list.Add(Cmd("export-data", Strings.Export_Command, file, () => _ = Portability().ExportAsync()));
        list.Add(Cmd("import-bundle", Strings.Bundle_Command, file, () => _ = Portability().ImportBundleAsync()));
        list.Add(Cmd("import-ynab-monarch", Strings.Ynab_Command, file, () => _ = Portability().ImportFromAppAsync()));

        // M9b: debt payoff planner, budget health report and PNG export.
        list.Add(Cmd("goals-debt", Strings.Debt_OpenPlanner, actions, () => Navigation().NavigateTo<GoalsViewModel>(GoalsTab.DebtPayoff)));
        list.Add(Cmd("reports-health", Strings.Health_OpenReport, actions, () => Navigation().NavigateTo<ReportsViewModel>(Keel.Desktop.ViewModels.Reports.ReportKind.BudgetHealth)));
        list.Add(Cmd("reports-export-png", Strings.ExportPng_Shortcut, actions, () => OnPage<ReportsViewModel>(shell, r => _ = r.ExportPngAsync())));

        AddCompleteness(shell, list);
        return list;
    }

    /// <summary>The menu tree: File, Edit, View, Go, Help (macOS moves About, Settings and Quit to the app menu).</summary>
    public IReadOnlyList<MenuNode> BuildMenu(ShellViewModel shell, bool macOS)
    {
        var commands = Build(shell).ToDictionary(c => c.Id, StringComparer.Ordinal);
        MenuNode Leaf(string id, string? header = null) => new(header ?? commands[id].Title, commands[id]);

        var fileItems = new List<MenuNode>
        {
            Leaf("new-file"), Leaf("open-file"), Leaf("move-file"), MenuNode.Separator,
            Leaf("import"), MenuNode.Separator,
            Leaf("backup"), Leaf("restore"),
        };
        if (!macOS)
        {
            fileItems.Add(MenuNode.Separator);
            fileItems.Add(Leaf("settings"));
            if (commands.ContainsKey("quit"))
            {
                fileItems.Add(Leaf("quit"));
            }
        }

        var goItems = commands.Values.Where(c => c.Id.StartsWith("go-", StringComparison.Ordinal)).Select(c => new MenuNode(c.Title, c)).ToList();
        goItems.Add(MenuNode.Separator);
        goItems.Add(Leaf("settings-Connections"));

        var helpItems = new List<MenuNode> { Leaf("shortcuts"), Leaf("guide"), MenuNode.Separator, Leaf("diagnostics"), Leaf("integrity") };
        if (!macOS)
        {
            helpItems.Add(MenuNode.Separator);
            helpItems.Add(Leaf("about"));
        }

        return
        [
            new(Strings.Menu_File, Children: fileItems),
            new(Strings.Menu_Edit, Children: [Leaf("undo"), Leaf("redo"), MenuNode.Separator, Leaf("search"), new(Strings.Shortcut_CommandPalette, new AppCommand("palette", Strings.Shortcut_CommandPalette, Strings.Palette_SectionEdit, () => _ = shell.OpenCommandPaletteAsync(), registry.KeysFor("palette"), registry.Find("palette")?.Gesture))]),
            new(Strings.Menu_View, Children:
            [
                Leaf("sidebar"), MenuNode.Separator,
                new(Strings.Menu_Theme, Children: [Leaf("theme-system", Strings.Settings_ThemeSystem), Leaf("theme-light", Strings.Settings_ThemeLight), Leaf("theme-dark", Strings.Settings_ThemeDark)]),
                new(Strings.Menu_Density, Children: [Leaf("density-comfortable", Strings.Settings_DensityComfortable), Leaf("density-compact", Strings.Settings_DensityCompact)]),
                MenuNode.Separator,
                Leaf("budget-three-months", Strings.BudgetMonths_Menu), Leaf("budget-flex", Strings.Flex_Menu),
            ]),
            new(Strings.Menu_Go, Children: goItems),
            new(Strings.Menu_Help, Children: helpItems),
        ];
    }

    /// <summary>The macOS application menu: About and Preferences (macOS adds Hide and Quit itself).</summary>
    public IReadOnlyList<MenuNode> BuildAppMenu(ShellViewModel shell)
    {
        var commands = Build(shell).ToDictionary(c => c.Id, StringComparer.Ordinal);
        return [new(Strings.Menu_About, commands["about"]), MenuNode.Separator, new(Strings.Menu_Preferences, commands["settings"])];
    }

    /// <summary>Settings sections reachable by name (x:Name "{name}Section" in SettingsView).</summary>
    public static IReadOnlyList<(string Name, string Title)> SettingsSections { get; } =
    [
        ("General", Strings.Settings_General),
        ("Appearance", Strings.Settings_Appearance),
        ("Connections", Strings.Settings_ConnectionsTitle),
        ("Bills", Strings.Settings_BillsTitle),
        ("Payees", Strings.Settings_Payees),
        ("Rules", Strings.Settings_Rules),
        ("Keyboard", Strings.Settings_Shortcuts),
        ("Updates", Strings.Settings_UpdatesTitle),
        ("Privacy", Strings.Stats_SectionTitle),
        ("Encryption", Strings.Encrypt_Section),
        ("Tags", Strings.Tag_SettingsTitle),
    ];

    private AppCommand Cmd(string id, string title, string section, Action execute)
    {
        var entry = registry.Find(id);
        return new AppCommand(id, title, section, execute, entry?.Keys, entry?.Gesture);
    }

    // M9 (F-SET-5, ADR 0103): every non-row action of every screen, and enablement for all commands. Page actions
    // navigate to their page first; enablement comes from the open file and the current page only, so building the
    // list never creates a page that is not shown.
    private void AddCompleteness(ShellViewModel shell, List<AppCommand> list)
    {
        var session = services.GetService<AppSession>();
        var hasFile = session?.BudgetFile is not null;
        var encrypted = session?.BudgetFile?.IsEncrypted ?? false;
        var go = Strings.Palette_SectionGoTo;
        var file = Strings.Palette_SectionFile;
        var view = Strings.Palette_SectionView;
        var actions = Strings.Palette_SectionActions;
        var help = Strings.Palette_SectionHelp;
        var register = shell.CurrentPage as AccountsViewModel;
        var accountRegister = register is { AccountId: not null } ? register : null;

        // Enablement of the M8 commands: file actions need an open file; undo, redo and sync ask their command.
        string[] needFile = ["move-file", "import", "backup", "restore", "integrity", "add-account", "add-transaction", "start-review", "budget-fund", "budget-move", "bills-detect", "bills-new", "goals-new", "reports-export"];
        for (var i = 0; i < list.Count; i++)
        {
            var command = list[i];
            var enabled = command.Id switch
            {
                "undo" => shell.UndoCommand.CanExecute(null),
                "redo" => shell.RedoCommand.CanExecute(null),
                "sync-all" => shell.SyncAllCommand.CanExecute(null),
                _ when command.Id.StartsWith("go-", StringComparison.Ordinal) || command.Id.StartsWith("account-", StringComparison.Ordinal) => hasFile,
                _ => !needFile.Contains(command.Id) || hasFile,
            };
            list[i] = command with { IsEnabled = enabled };
        }

        // Go to: the rules page, each report, each Bills tab.
        list.Add(Cmd("go-rules", Strings.Palette_GoRules, go, () => shell.NavigateTo<RulesViewModel>()) with { IsEnabled = hasFile });
        ReportKind[] reports = [ReportKind.Spending, ReportKind.IncomeExpense, ReportKind.NetWorth, ReportKind.Forecast];
        string[] reportTitles = [Strings.Reports_Spending_Title, Strings.Reports_IncomeExpense_Title, Strings.Reports_NetWorth_Title, Strings.Forecast_Title];
        for (var i = 0; i < reports.Length; i++)
        {
            var kind = reports[i];
            list.Add(Cmd(Numbered("reports-pick", i), LedgerText.Format(Strings.Palette_Report, reportTitles[i]), go, () => Navigate<ReportsViewModel>(kind)) with { Keys = Digit(i), IsEnabled = hasFile });
        }

        string[] tabs = [Strings.Bills_TabCalendar, Strings.Bills_TabList, Strings.Bills_TabSubscriptions];
        for (var i = 0; i < tabs.Length; i++)
        {
            var tab = (BillsTab)i;
            list.Add(Cmd(Numbered("bills-tabs", i), LedgerText.Format(Strings.Palette_BillsTab, tabs[i]), go, () => OnPage<BillsViewModel>(shell, b => b.SelectedTab = tab)) with { Keys = Digit(i), IsEnabled = hasFile });
        }

        // File: restore from a file, encryption (F-SET-4).
        list.Add(Cmd("restore-file", Strings.Palette_RestoreFromFile, file, () => _ = DataFile().RestoreFromFileCommand.ExecuteAsync(null)) with { IsEnabled = hasFile });
        list.Add(Cmd("encrypt-file", Strings.Encrypt_EncryptButton, file, () => _ = Encryption().EncryptAsync()) with { IsEnabled = hasFile && !encrypted });
        list.Add(Cmd("remove-encryption", Strings.Encrypt_RemoveButton, file, () => _ = Encryption().RemoveEncryptionAsync()) with { IsEnabled = hasFile && encrypted });
        list.Add(Cmd("unlock-file", Strings.Encrypt_UnlockButton, file, () => _ = shell.UnlockAsync()) with { IsEnabled = shell.LockedFile is not null });

        // View: closed accounts, notifications, accent colours, motion.
        list.Add(Cmd("closed-accounts", shell.ShowClosedAccounts ? Strings.Palette_HideClosedAccounts : Strings.Palette_ShowClosedAccounts, view, () => shell.ShowClosedAccounts = !shell.ShowClosedAccounts) with { IsEnabled = shell.HasClosedAccounts || shell.ShowClosedAccounts });
        list.Add(Cmd("notifications", Strings.Palette_ShowNotifications, view, () => shell.Notifications?.ToggleCommand.Execute(null)) with { IsEnabled = shell.Notifications is not null && hasFile });
        foreach (var accent in AppearanceService.Accents.Keys)
        {
            var name = Strings.ResourceManager.GetString("Accent_" + accent, Strings.Culture) ?? accent.ToString();
            list.Add(Cmd("accent-" + accent.ToString().ToLowerInvariant(), LedgerText.Format(Strings.Palette_Accent, name), view, () =>
            {
                var appearance = Appearance();
                appearance.SelectedAccent = appearance.Accents.First(a => a.Accent == accent);
            }));
        }

        string[] motions = [Strings.Settings_MotionSystem, Strings.Settings_MotionReduce, Strings.Settings_MotionFull];
        string[] motionIds = ["motion-system", "motion-reduce", "motion-full"];
        for (var i = 0; i < motions.Length; i++)
        {
            var index = i;
            list.Add(Cmd(motionIds[i], LedgerText.Format(Strings.Palette_Motion, motions[i]), view, () => Appearance().MotionIndex = index));
        }

        // Register (the account register on screen).
        list.Add(Cmd("reconcile", Strings.Palette_Reconcile, actions, () => accountRegister?.StartReconcileCommand.Execute(null)) with { IsEnabled = accountRegister is { CanReconcile: true, IsTracking: false } });
        list.Add(Cmd("edit-account", Strings.Palette_EditAccount, actions, () => Run(accountRegister?.EditAccountCommand)) with { IsEnabled = accountRegister?.Account is not null });
        list.Add(Cmd("record-balance", Strings.Palette_RecordBalance, actions, () => Run(accountRegister?.RecordBalanceCommand)) with { IsEnabled = accountRegister is { IsTracking: true } });
        list.Add(Cmd("clear-filters", Strings.Palette_ClearFilters, actions, () => register?.ClearFiltersCommand.Execute(null)) with { IsEnabled = register is not null });
        list.Add(Cmd("sync-account", Strings.Palette_SyncAccount, actions, () => Run(accountRegister?.SyncAccountCommand)) with { IsEnabled = accountRegister?.SyncAccountCommand.CanExecute(null) ?? false });
        list.Add(Cmd("reconnect-account", Strings.Palette_ReconnectAccount, actions, () => Run(accountRegister?.ReconnectAccountCommand)) with { IsEnabled = accountRegister is { NeedsReconnect: true } });
        list.Add(Cmd("schedule-new", Strings.Palette_NewSchedule, actions, () => Run(accountRegister?.Scheduled?.NewScheduleCommand)) with { IsEnabled = accountRegister?.Scheduled is not null && accountRegister.Account is not null });

        // Budget.
        list.Add(Cmd("budget-previous", Strings.Shortcut_BudgetPreviousMonth, actions, () => OnPage<BudgetViewModel>(shell, b => b.PreviousMonth())) with { IsEnabled = hasFile });
        list.Add(Cmd("budget-next", Strings.Shortcut_BudgetNextMonth, actions, () => OnPage<BudgetViewModel>(shell, b => b.NextMonth())) with { IsEnabled = hasFile });
        list.Add(Cmd("budget-this-month", Strings.Palette_BudgetThisMonth, actions, () => OnPage<BudgetViewModel>(shell, b => b.GoToToday())) with { IsEnabled = hasFile });
        list.Add(Cmd("budget-inspector", Strings.Shortcut_BudgetInspector, actions, () => OnPage<BudgetViewModel>(shell, b => b.ToggleInspector())) with { IsEnabled = hasFile });
        list.Add(Cmd("budget-quick-assign", Strings.Shortcut_BudgetQuickAssign, actions, () => OnPage<BudgetViewModel>(shell, b => _ = b.QuickAssignPaletteAsync())) with { IsEnabled = hasFile });
        list.Add(Cmd("budget-explain-rta", Strings.Palette_ExplainReadyToAssign, actions, () => OnPage<BudgetViewModel>(shell, b => b.ExplainReadyToAssign())) with { IsEnabled = hasFile });
        list.Add(Cmd("budget-manage-categories", Strings.Palette_ManageCategories, actions, () => OnPage<BudgetViewModel>(shell, b => _ = b.ManageCategoriesAsync())) with { IsEnabled = hasFile });

        // Review and rules.
        list.Add(Cmd("review-batch", Strings.Palette_BatchApprove, actions, () => OnPage<ReviewViewModel>(shell, r => Run(r.BatchApproveCommand))) with { IsEnabled = hasFile });
        list.Add(Cmd("rules-new", Strings.Palette_NewRule, actions, () => OnPage<RulesViewModel>(shell, r => Run(r.NewRuleCommand))) with { IsEnabled = hasFile });
        list.Add(Cmd("rules-apply-all", Strings.Palette_ApplyAllRules, actions, () => OnPage<RulesViewModel>(shell, r => Run(r.ApplyAllCommand))) with { IsEnabled = hasFile });

        // Bills calendar months.
        list.Add(Cmd("bills-previous-month", Strings.Palette_BillsPreviousMonth, actions, () => OnPage<BillsViewModel>(shell, b => _ = b.PreviousMonthAsync())) with { IsEnabled = hasFile });
        list.Add(Cmd("bills-next-month", Strings.Palette_BillsNextMonth, actions, () => OnPage<BillsViewModel>(shell, b => _ = b.NextMonthAsync())) with { IsEnabled = hasFile });
        list.Add(Cmd("bills-this-month", Strings.Palette_BillsThisMonth, actions, () => OnPage<BillsViewModel>(shell, b => _ = b.ThisMonthAsync())) with { IsEnabled = hasFile });

        // Reports.
        list.Add(Cmd("reports-refresh", Strings.Palette_RefreshReport, actions, () => OnPage<ReportsViewModel>(shell, r => r.Reload())) with { IsEnabled = hasFile });
        list.Add(Cmd("reports-all-accounts", Strings.Palette_ReportAllAccounts, actions, () => OnPage<ReportsViewModel>(shell, r => r.SelectAllAccounts())) with { IsEnabled = hasFile });

        // Home, notifications, connections, updates.
        list.Add(Cmd("home-hide-checklist", Strings.Palette_HideChecklist, actions, () => OnPage<HomeViewModel>(shell, h => _ = h.DismissChecklistAsync())) with { IsEnabled = hasFile && shell.CurrentPage is HomeViewModel { ShowChecklist: true } });
        list.Add(Cmd("notifications-read", Strings.Palette_MarkNotificationsRead, actions, () => _ = shell.Notifications?.MarkAllReadAsync()) with { IsEnabled = shell.Notifications is not null && hasFile });
        list.Add(Cmd("add-connection", Strings.Palette_AddConnection, actions, () => _ = shell.Sync.AddConnectionAsync()) with { IsEnabled = hasFile });
        var updates = services.GetService<UpdateService>();
        list.Add(Cmd("check-updates", Strings.Palette_CheckUpdates, help, () => Run(Updates().CheckNowCommand)) with { IsEnabled = updates?.IsEnabled ?? false });
        list.Add(Cmd("install-update", Strings.Palette_InstallUpdate, help, () => Run(Updates().InstallCommand)) with { IsEnabled = updates?.IsUpdateAvailable ?? false });
    }

    private static string Numbered(string id, int index) => id + "-" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Digit(int index) => (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void Navigate<TPage>(object parameter)
        where TPage : class => services.GetRequiredService<Keel.Application.Navigation.INavigationService>().NavigateTo<TPage>(parameter);

    private EncryptionSettingsViewModel Encryption() => services.GetRequiredService<EncryptionSettingsViewModel>();

    private UpdatesSettingsViewModel Updates() => services.GetRequiredService<UpdatesSettingsViewModel>();

    private DataFileSettingsViewModel DataFile() => services.GetRequiredService<DataFileSettingsViewModel>();

    private Keel.Application.Navigation.INavigationService Navigation() => services.GetRequiredService<Keel.Application.Navigation.INavigationService>();

    private AppearanceSettingsViewModel Appearance() => services.GetRequiredService<AppearanceSettingsViewModel>();

    private Keel.Desktop.ViewModels.Portability.PortabilitySettingsViewModel Portability() => services.GetRequiredService<Keel.Desktop.ViewModels.Portability.PortabilitySettingsViewModel>();

    private static void Run(System.Windows.Input.ICommand? command)
    {
        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void OnPage<TPage>(ShellViewModel shell, Action<TPage> action)
        where TPage : class
    {
        shell.NavigateTo<TPage>();
        action(services.GetRequiredService<TPage>());
    }

    private static void Quit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow?.Close();
        }
    }
}
