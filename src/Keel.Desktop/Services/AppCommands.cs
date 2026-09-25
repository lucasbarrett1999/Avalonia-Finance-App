using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Keel.Application.Settings;
using Keel.Desktop.Resources;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Dialogs;
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
    /// <summary>Screen-reader text: title, section and shortcut.</summary>
    public string AutomationName => Keys is null ? $"{Title}, {Section}" : $"{Title}, {Section}, {Keys}";
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
    ];

    private AppCommand Cmd(string id, string title, string section, Action execute)
    {
        var entry = registry.Find(id);
        return new AppCommand(id, title, section, execute, entry?.Keys, entry?.Gesture);
    }

    private DataFileSettingsViewModel DataFile() => services.GetRequiredService<DataFileSettingsViewModel>();

    private Keel.Application.Navigation.INavigationService Navigation() => services.GetRequiredService<Keel.Application.Navigation.INavigationService>();

    private AppearanceSettingsViewModel Appearance() => services.GetRequiredService<AppearanceSettingsViewModel>();

    private Keel.Desktop.ViewModels.Portability.PortabilitySettingsViewModel Portability() => services.GetRequiredService<Keel.Desktop.ViewModels.Portability.PortabilitySettingsViewModel>();

    private static void Run(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
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
