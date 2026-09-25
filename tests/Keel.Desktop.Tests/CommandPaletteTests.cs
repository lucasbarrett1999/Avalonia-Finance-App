using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.Views;
using Keel.Domain;

namespace Keel.Desktop.Tests;

/// <summary>The command palette, the shortcut registry, page keys and the menus (F-SET-5, PRD 9.1, PRD 8).</summary>
public sealed class CommandPaletteTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private async Task<(ShellWindow Window, ShellViewModel Shell)> ShowAsync()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private static RawInputModifiers Command => PlatformShortcuts.FromCurrentPlatform().Command();

    [AvaloniaFact]
    public async Task Ctrl_K_opens_the_palette_fuzzy_search_finds_a_page_and_enter_goes_there()
    {
        var (window, shell) = await ShowAsync();
        window.Press(PhysicalKey.K, Command);
        var palette = await ImportDialogTests.DialogAsync<CommandPaletteViewModel>(shell);
        var view = window.GetVisualDescendants().OfType<Keel.Desktop.Views.Dialogs.CommandPaletteView>().Single();
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() == view.Named<TextBox>("QueryBox"), "focus in the search box");

        window.Type("bdgt");
        palette.Results[0].Title.ShouldBe(Keel.Desktop.Resources.Strings.Nav_Budget, string.Join(" | ", palette.Results.Take(3).Select(r => r.Title + " " + r.Section)));
        palette.Selected.ShouldBeSameAs(palette.Results[0]);
        window.Press(PhysicalKey.Enter);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is null, "palette closed");
        shell.CurrentPage.ShouldBeOfType<BudgetViewModel>();

        // Arrow keys move, Esc closes without running anything.
        window.Press(PhysicalKey.K, Command);
        palette = await ImportDialogTests.DialogAsync<CommandPaletteViewModel>(shell);
        var first = palette.Selected;
        window.Press(PhysicalKey.ArrowDown);
        palette.Selected.ShouldNotBeSameAs(first);
        window.Press(PhysicalKey.ArrowUp);
        palette.Selected.ShouldBeSameAs(first);
        window.Type("zzqqxx");
        palette.HasNoResults.ShouldBeTrue();
        window.Press(PhysicalKey.Escape);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is null, "palette cancelled");
        shell.CurrentPage.ShouldBeOfType<BudgetViewModel>();
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_palette_lists_every_page_account_settings_section_and_general_action()
    {
        await Task.Run(() => _host.Get<IAccountService>().CreateAccountAsync(new CreateAccountRequest("Joint checking", AccountType.Checking, "USD", new DateOnly(2026, 1, 1), 0), CancellationToken.None));
        var (window, shell) = await ShowAsync();
        await shell.AccountsLoading;
        await UiTestHelpers.WaitUntilAsync(() => shell.AccountGroups.SelectMany(g => g.Accounts).Any(), "account in the sidebar");
        var commands = _host.Get<AppCommands>().Build(shell);

        var titles = commands.Select(c => c.Title).ToList();
        foreach (var item in shell.AllItems)
        {
            titles.ShouldContain(item.Title);
        }

        titles.ShouldContain(t => t.Contains("Joint checking", StringComparison.Ordinal));
        foreach (var (section, _) in AppCommands.SettingsSections)
        {
            commands.ShouldContain(c => c.Id == "settings-" + section);
        }

        // Every general and connection shortcut is also a palette command showing the same keys.
        var registry = _host.Get<ShortcutRegistry>();
        foreach (var entry in registry.All.Where(e => e.Scope is ShortcutScope.General or ShortcutScope.Connections && e.Id is not "palette" and not "redo-alt"))
        {
            var command = commands.SingleOrDefault(c => c.Id == entry.Id);
            command.ShouldNotBeNull($"palette command for shortcut '{entry.Id}'");
            command.Keys.ShouldBe(entry.Keys);
        }

        // Each screen with actions contributes at least one.
        foreach (var id in new[] { "add-transaction", "start-review", "budget-fund", "budget-move", "bills-new", "bills-detect", "goals-new", "reports-export", "backup", "open-file", "new-file" })
        {
            commands.ShouldContain(c => c.Id == id);
        }

        commands.Select(c => c.Id).ShouldBeUnique();
        commands.ShouldAllBe(c => !string.IsNullOrWhiteSpace(c.AutomationName));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Palette_actions_run_after_it_closes()
    {
        var (window, shell) = await ShowAsync();
        var run = shell.OpenCommandPaletteAsync();
        var palette = await ImportDialogTests.DialogAsync<CommandPaletteViewModel>(shell);
        palette.Query = "keyboard shortcuts";
        palette.Selected!.Id.ShouldBe("shortcuts");
        palette.ConfirmCommand.Execute(null);
        await run;
        shell.CurrentPage.ShouldBeOfType<SettingsViewModel>();
        _host.Get<SettingsViewModel>().RequestedSection.ShouldBe("Keyboard");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_keyboard_reference_is_generated_from_the_registry_and_every_key_binding_is_registered()
    {
        var (window, shell) = await ShowAsync();
        var registry = _host.Get<ShortcutRegistry>();
        var settings = _host.Get<SettingsViewModel>();

        settings.ShortcutGroups.SelectMany(g => g.Items).Select(i => (i.Action, i.Keys))
            .ShouldBe(registry.All.Select(e => (e.Action, e.Keys)));
        settings.ShortcutGroups.Select(g => g.Title).ShouldBe(
            [.. new[] { ShortcutScope.General, ShortcutScope.Register, ShortcutScope.Budget, ShortcutScope.Review, ShortcutScope.Bills, ShortcutScope.Reports, ShortcutScope.Goals, ShortcutScope.Connections, ShortcutScope.Dialogs }
                .Select(ShortcutRegistry.ScopeTitle)]);
        registry.All.Select(e => e.Id).ShouldBeUnique();

        // The window's own key bindings are all in the registry.
        var gestures = registry.All.Where(e => e.Gesture is not null).Select(e => e.Gesture!).ToList();
        foreach (var binding in window.KeyBindings)
        {
            gestures.ShouldContain(binding.Gesture, $"{binding.Gesture} is bound on the window but not in the registry");
        }

        // The rendered reference shows them.
        shell.NavigateToSettings("Keyboard");
        Dispatcher.UIThread.RunJobs();
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToHashSet();
        texts.ShouldContain(registry.Find("palette")!.Keys);
        texts.ShouldContain(ShortcutRegistry.ScopeTitle(ShortcutScope.Connections));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ctrl_digit_navigates_and_page_keys_work_on_bills_goals_and_reports()
    {
        var (window, shell) = await ShowAsync();
        window.Press(PhysicalKey.Digit2, Command);
        shell.CurrentPage.ShouldBeOfType<BudgetViewModel>();
        window.Press(PhysicalKey.Digit1, Command);
        shell.CurrentPage.ShouldBeOfType<HomeViewModel>();

        // Reports: 1–4 choose a report.
        window.Press(PhysicalKey.Digit6, Command);
        var reports = shell.CurrentPage.ShouldBeOfType<ReportsViewModel>();
        await UiTestHelpers.WaitUntilAsync(() => reports.Loading.IsCompleted, "reports loaded");
        window.Press(PhysicalKey.Digit3);
        reports.SelectedReport.ShouldBeSameAs(reports.Reports[2]);

        // Bills: 2 shows the list tab, N opens the new-item editor.
        window.Press(PhysicalKey.Digit4, Command);
        var bills = shell.CurrentPage.ShouldBeOfType<BillsViewModel>();
        window.Press(PhysicalKey.Digit2);
        ((int)bills.SelectedTab).ShouldBe(1);
        window.Press(PhysicalKey.N);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is not null, "new bill dialog");
        shell.Dialogs.Current!.CancelCommand.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is null, "dialog closed");

        // Goals: N starts the wizard.
        window.Press(PhysicalKey.Digit5, Command);
        shell.CurrentPage.ShouldBeOfType<GoalsViewModel>();
        window.Press(PhysicalKey.N);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is not null, "new goal wizard");
        shell.Dialogs.Current!.CancelCommand.Execute(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Menus_have_the_in_window_bar_here_and_the_mac_app_menu_layout()
    {
        var (window, shell) = await ShowAsync();
        var commands = _host.Get<AppCommands>();

        // Windows and Linux: an in-window menu bar with File, Edit, View, Go, Help.
        var menu = window.GetVisualDescendants().OfType<Menu>().Single(m => m.Name == "MainMenu");
        menu.IsVisible.ShouldBe(!OperatingSystem.IsMacOS());
        var desktop = commands.BuildMenu(shell, macOS: false);
        desktop.Select(n => n.Header).ShouldBe([Keel.Desktop.Resources.Strings.Menu_File, Keel.Desktop.Resources.Strings.Menu_Edit, Keel.Desktop.Resources.Strings.Menu_View, Keel.Desktop.Resources.Strings.Menu_Go, Keel.Desktop.Resources.Strings.Menu_Help]);
        Ids(desktop).ShouldContain("settings");
        Ids(desktop).ShouldContain("about");
        if (!OperatingSystem.IsMacOS())
        {
            menu.Items.Count.ShouldBe(5);
            menu.Items.OfType<MenuItem>().ShouldAllBe(i => !string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(i)));
        }

        // macOS: About and Preferences move to the app menu (macOS adds Quit there itself).
        var mac = commands.BuildMenu(shell, macOS: true);
        Ids(mac).ShouldNotContain("about");
        Ids(mac).ShouldNotContain("settings");
        Ids(mac).ShouldNotContain("quit");
        var app = commands.BuildAppMenu(shell);
        Ids(app).ShouldBe(["about", "settings"]);
        MenuBuilder.ToNativeMenu(mac).Items.Count.ShouldBe(5);

        // Every leaf runs: About opens its dialog.
        Leaf(desktop, "about").Command!.Execute();
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is AboutDialogViewModel, "About shown");
        ((AboutDialogViewModel)shell.Dialogs.Current!).VersionText.ShouldContain(KeelInfo.Version);
        window.Close();
    }

    [AvaloniaFact]
    public void The_user_guide_lists_every_registered_shortcut()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Keel.sln")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Keel.sln not found");
        }

        var guide = File.ReadAllText(Path.Combine(root, "docs", "user-guide", "keyboard-shortcuts.md"));
        foreach (var entry in new ShortcutRegistry(PlatformShortcuts.FromCurrentPlatform()).All.Where(e => !e.Id.StartsWith("go-", StringComparison.Ordinal)))
        {
            guide.ShouldContain(entry.Action, Case.Sensitive, $"docs/user-guide/keyboard-shortcuts.md is missing '{entry.Action}'");
        }
    }

    [Theory]
    [InlineData("bdgt", "Budget", true)]
    [InlineData("BUD", "Budget", true)]
    [InlineData("sync all", "Sync all linked accounts", true)]
    [InlineData("tgbd", "Budget", false)]
    [InlineData("xyz", "Budget", false)]
    [InlineData("", "Anything", true)]
    public void Fuzzy_match_needs_the_characters_in_order(string query, string text, bool matches) =>
        FuzzyMatch.Score(query, text).HasValue.ShouldBe(matches);

    [Fact]
    public void Fuzzy_match_prefers_word_starts_and_runs_over_scattered_letters()
    {
        string[] items = ["Show a budget", "Back up now", "Budget", "Bills: new bill"];
        FuzzyMatch.Filter(items, "bu", s => s)[0].ShouldBe("Budget");
        FuzzyMatch.Filter(items, "back", s => s).ShouldBe(["Back up now"]);
        FuzzyMatch.Filter(items, "nb", s => s)[0].ShouldBe("Bills: new bill");
        FuzzyMatch.Filter(items, null, s => s).ShouldBe(items);
    }

    // Shortcuts that act on the selected row, the cell cursor, an open editor or a dialog; everything else in the
    // registry must have a palette command (ADR 0103).
    private static readonly Dictionary<string, string> KeysOutsideThePalette = new(StringComparer.Ordinal)
    {
        ["palette"] = "opens the palette itself",
        ["register-edit"] = "edits the selected transaction",
        ["register-cleared"] = "toggles the selected transaction",
        ["register-approve"] = "approves the selected transactions",
        ["register-delete"] = "deletes the selected transactions",
        ["register-save-new"] = "inside the transaction editor",
        ["register-cancel"] = "inside the transaction editor",
        ["register-tag-add"] = "inside the transaction editor's Tags box",
        ["register-tag-remove"] = "inside the transaction editor's Tags box",
        ["budget-navigate"] = "moves the budget cell cursor",
        ["budget-edit"] = "edits the budget cell under the cursor",
        ["budget-next-assigned"] = "inside the Assigned editor",
        ["budget-target"] = "sets the target of the category under the cursor",
        ["review-approve"] = "decides the focused review item",
        ["review-pick"] = "decides the focused review item",
        ["review-category"] = "decides the focused review item",
        ["review-split"] = "decides the focused review item",
        ["review-transfer"] = "decides the focused review item",
        ["review-rule"] = "decides the focused review item",
        ["review-delete"] = "decides the focused review item",
        ["review-move"] = "moves between review items",
        ["bills-close"] = "closes the selected item's detail",
        ["dialog-confirm"] = "inside dialogs",
        ["dialog-cancel"] = "inside dialogs",
        ["money-math"] = "inside amount boxes",
    };

    // Registry ids whose palette command has another id.
    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.Ordinal)
    {
        ["register-new"] = "add-transaction",
        ["redo-alt"] = "redo",
    };

    // Every parameterless command of every screen, mapped to the palette command that runs it (ADR 0103).
    private static readonly Dictionary<string, string> CoveredCommands = new(StringComparer.Ordinal)
    {
        ["ShellViewModel.ToggleSidebarCommand"] = "sidebar",
        ["ShellViewModel.UndoCommand"] = "undo",
        ["ShellViewModel.RedoCommand"] = "redo",
        ["ShellViewModel.AddAccountCommand"] = "add-account",
        ["ShellViewModel.SearchCommand"] = "search",
        ["ShellViewModel.SyncAllCommand"] = "sync-all",
        ["HomeViewModel.AssignCommand"] = "go-1",
        ["HomeViewModel.StartReviewCommand"] = "start-review",
        ["HomeViewModel.OpenNetWorthCommand"] = "reports-pick-3",
        ["HomeViewModel.OpenAccountsCommand"] = "go-6",
        ["HomeViewModel.AddAccountCommand"] = "add-account",
        ["HomeViewModel.OpenBillsCommand"] = "go-3",
        ["HomeViewModel.OpenForecastCommand"] = "reports-pick-4",
        ["HomeViewModel.DismissChecklistCommand"] = "home-hide-checklist",
        ["BudgetViewModel.FundTargetsCommand"] = "budget-fund",
        ["BudgetViewModel.ToggleInspectorCommand"] = "budget-inspector",
        ["BudgetViewModel.ExplainReadyToAssignCommand"] = "budget-explain-rta",
        ["BudgetViewModel.QuickAssignPaletteCommand"] = "budget-quick-assign",
        ["BudgetViewModel.ManageCategoriesCommand"] = "budget-manage-categories",
        ["BudgetViewModel.UndoCommand"] = "undo",
        ["BudgetViewModel.PreviousMonthCommand"] = "budget-previous",
        ["BudgetViewModel.NextMonthCommand"] = "budget-next",
        ["BudgetViewModel.GoToTodayCommand"] = "budget-this-month",
        ["ReviewViewModel.BatchApproveCommand"] = "review-batch",
        ["ReviewViewModel.ManageRulesCommand"] = "go-rules",
        ["BillsViewModel.RunDetectionCommand"] = "bills-detect",
        ["BillsViewModel.AddItemCommand"] = "bills-new",
        ["BillsViewModel.PreviousMonthCommand"] = "bills-previous-month",
        ["BillsViewModel.NextMonthCommand"] = "bills-next-month",
        ["BillsViewModel.ThisMonthCommand"] = "bills-this-month",
        ["GoalsViewModel.NewGoalCommand"] = "goals-new",
        ["GoalsViewModel.OpenBudgetCommand"] = "go-1",
        ["ReportsViewModel.ReloadCommand"] = "reports-refresh",
        ["ReportsViewModel.SelectAllAccountsCommand"] = "reports-all-accounts",
        ["ReportsViewModel.ExportCsvCommand"] = "reports-export",
        ["ReportsViewModel.ExportPngCommand"] = "reports-export-png",
        ["HomeViewModel.OpenBudgetHealthCommand"] = "reports-health",
        ["BudgetViewModel.ToggleThreeMonthsCommand"] = "budget-three-months",
        ["BudgetViewModel.ToggleFlexViewCommand"] = "budget-flex",
        ["AccountsViewModel.EditTagsCommand"] = "register-tags",
        ["AccountsViewModel.NewTransactionCommand"] = "add-transaction",
        ["AccountsViewModel.StartReconcileCommand"] = "reconcile",
        ["AccountsViewModel.ImportFileCommand"] = "import",
        ["AccountsViewModel.EditAccountCommand"] = "edit-account",
        ["AccountsViewModel.RecordBalanceCommand"] = "record-balance",
        ["AccountsViewModel.ClearFiltersCommand"] = "clear-filters",
        ["AccountsViewModel.SyncAccountCommand"] = "sync-account",
        ["AccountsViewModel.ReconnectAccountCommand"] = "reconnect-account",
        ["ScheduledGhostsViewModel.NewScheduleCommand"] = "schedule-new",
        ["RulesViewModel.NewRuleCommand"] = "rules-new",
        ["RulesViewModel.ApplyAllCommand"] = "rules-apply-all",
        ["NotificationCenterViewModel.ToggleCommand"] = "notifications",
        ["NotificationCenterViewModel.MarkAllReadCommand"] = "notifications-read",
        ["ConnectionsSettingsViewModel.AddConnectionCommand"] = "add-connection",
        ["DataFileSettingsViewModel.NewFileCommand"] = "new-file",
        ["DataFileSettingsViewModel.OpenFileCommand"] = "open-file",
        ["DataFileSettingsViewModel.ChangeLocationCommand"] = "move-file",
        ["DataFileSettingsViewModel.BackupNowCommand"] = "backup",
        ["DataFileSettingsViewModel.RestoreFromFileCommand"] = "restore-file",
        ["DataFileSettingsViewModel.CheckIntegrityCommand"] = "integrity",
        ["DataFileSettingsViewModel.CopyDiagnosticBundleCommand"] = "diagnostics",
        ["UpdatesSettingsViewModel.CheckNowCommand"] = "check-updates",
        ["UpdatesSettingsViewModel.InstallCommand"] = "install-update",
        ["EncryptionSettingsViewModel.EncryptCommand"] = "encrypt-file",
        ["EncryptionSettingsViewModel.RemoveEncryptionCommand"] = "remove-encryption",
        ["EncryptionSettingsViewModel.UnlockCommand"] = "unlock-file",
        ["StatsSettingsViewModel.RefreshCommand"] = "settings-Privacy",
    };

    // Screen commands that are row-level (they act on a selected or focused item), belong to an editor, form or popup,
    // or retry a failed load. They stay buttons and keys only.
    private static readonly Dictionary<string, string> CommandsOutsideThePalette = new(StringComparer.Ordinal)
    {
        ["ShellViewModel.OpenCommandPaletteCommand"] = "opens the palette itself",
        ["BudgetViewModel.SetTargetCommand"] = "the category under the cursor",
        ["BudgetViewModel.PickerPreviousYearCommand"] = "inside the month picker",
        ["BudgetViewModel.PickerNextYearCommand"] = "inside the month picker",
        ["BudgetViewModel.RetryCommand"] = "retries a failed load",
        ["ReviewViewModel.ApproveCommand"] = "the focused review item",
        ["ReviewViewModel.ChangeCategoryCommand"] = "the focused review item",
        ["ReviewViewModel.SplitCommand"] = "the focused review item",
        ["ReviewViewModel.MarkTransferCommand"] = "the focused review item",
        ["ReviewViewModel.CreateRuleCommand"] = "the focused review item",
        ["ReviewViewModel.DeleteCommand"] = "the focused review item",
        ["ReviewViewModel.NextCommand"] = "moves between review items",
        ["ReviewViewModel.PreviousCommand"] = "moves between review items",
        ["ReviewViewModel.ToggleTraceCommand"] = "the focused review item's explanation",
        ["ReviewViewModel.RetryCommand"] = "retries a failed load",
        ["BillsViewModel.EditCommand"] = "the selected bill",
        ["BillsViewModel.CloseDetailCommand"] = "the selected bill",
        ["BillsViewModel.ConfirmCommand"] = "the selected bill",
        ["BillsViewModel.PauseCommand"] = "the selected bill",
        ["BillsViewModel.ResumeCommand"] = "the selected bill",
        ["BillsViewModel.DismissCommand"] = "the selected bill",
        ["BillsViewModel.ReenableCommand"] = "the selected bill",
        ["BillsViewModel.CreateTargetCommand"] = "the selected bill",
        ["BillsViewModel.CreateScheduleCommand"] = "the selected bill",
        ["BillsViewModel.OpenTransactionsCommand"] = "the selected bill",
        ["AccountsViewModel.EditSelectedCommand"] = "the selected transaction",
        ["AccountsViewModel.SaveCommand"] = "the transaction editor",
        ["AccountsViewModel.SaveAndNewCommand"] = "the transaction editor",
        ["AccountsViewModel.CancelEditCommand"] = "the transaction editor",
        ["AccountsViewModel.ToggleClearedCommand"] = "the selected transaction",
        ["AccountsViewModel.ApproveCommand"] = "the selected transactions",
        ["AccountsViewModel.DeleteSelectedCommand"] = "the selected transactions",
        ["AccountsViewModel.MarkClearedCommand"] = "the selected transactions",
        ["AccountsViewModel.MarkUnclearedCommand"] = "the selected transactions",
        ["AccountsViewModel.CategorizeSelectedCommand"] = "the selected transactions",
        ["AccountsViewModel.MoveSelectedCommand"] = "the selected transactions",
        ["AccountsViewModel.CreateRuleFromTransactionCommand"] = "the selected transaction",
        ["AccountsViewModel.RetryCommand"] = "retries a failed load",
        ["ScheduledGhostsViewModel.ToggleExpandedCommand"] = "expands the register's schedule strip",
        ["RulesViewModel.RetryCommand"] = "retries a failed load",
        ["NotificationCenterViewModel.CloseCommand"] = "closes the open panel",
        ["ConnectionsSettingsViewModel.SaveClientIdCommand"] = "the key typed into the Connections form",
        ["ConnectionsSettingsViewModel.SaveSecretCommand"] = "the key typed into the Connections form",
        ["ConnectionsSettingsViewModel.SaveSetupTokenCommand"] = "the token typed into the Connections form",
        ["ConnectionsSettingsViewModel.ReplaceClientIdCommand"] = "a field of the Connections form",
        ["ConnectionsSettingsViewModel.ReplaceSecretCommand"] = "a field of the Connections form",
        ["ConnectionsSettingsViewModel.ReplaceSetupTokenCommand"] = "a field of the Connections form",
        ["ConnectionsSettingsViewModel.CancelReplaceCommand"] = "a field of the Connections form",
        ["ConnectionsSettingsViewModel.RemovePlaidKeysCommand"] = "the keys shown in the Connections form",
        ["BudgetViewModel.ClearFlexFilterCommand"] = "the Flex drill-down banner on the grid",
        ["PayeesViewModel.MergeCommand"] = "the selected payees",
        ["PayeesViewModel.ClearSelectionCommand"] = "the selected payees",
        ["RenameTagDialogViewModel.ConfirmCommand"] = "the rename tag dialog",
        ["RenameTagDialogViewModel.CancelCommand"] = "the rename tag dialog",
        ["MergeTagDialogViewModel.ConfirmCommand"] = "the merge tag dialog",
        ["MergeTagDialogViewModel.CancelCommand"] = "the merge tag dialog",
    };

    [AvaloniaFact]
    public async Task Every_non_row_action_of_every_screen_and_every_shortcut_has_a_palette_command()
    {
        var (window, shell) = await ShowAsync();
        var commands = _host.Get<AppCommands>().Build(shell);
        var ids = commands.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        // Every registry shortcut has a palette command with its id (or numbered ones for "1–4"-style keys).
        foreach (var entry in _host.Get<ShortcutRegistry>().All.Where(e => !KeysOutsideThePalette.ContainsKey(e.Id)))
        {
            var id = KeyAliases.GetValueOrDefault(entry.Id, entry.Id);
            if (!ids.Contains(id) && !ids.Any(c => c.StartsWith(id + "-", StringComparison.Ordinal)))
            {
                problems.Add($"shortcut '{entry.Id}' ({entry.Action}) has no palette command");
            }
        }

        // Every parameterless command of every screen is in the palette or classified as row/editor-level.
        var assembly = typeof(ShellViewModel).Assembly;
        Type[] extra = [typeof(ShellViewModel), typeof(Keel.Desktop.ViewModels.Alerts.NotificationCenterViewModel), typeof(Keel.Desktop.ViewModels.Sync.ConnectionsSettingsViewModel), typeof(Keel.Desktop.ViewModels.Rules.PayeesViewModel), typeof(Keel.Desktop.ViewModels.Bills.ScheduledGhostsViewModel)];
        var screens = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && (typeof(PageViewModel).IsAssignableFrom(t) || t.Namespace == "Keel.Desktop.ViewModels.Settings" || extra.Contains(t)))
            .ToList();
        screens.Count.ShouldBeGreaterThan(15);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in screens)
        {
            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.PropertyType != typeof(CommunityToolkit.Mvvm.Input.IRelayCommand) && property.PropertyType != typeof(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand))
                {
                    continue; // IRelayCommand<T> takes the row it acts on
                }

                var key = type.Name + "." + property.Name;
                found.Add(key);
                if (CoveredCommands.TryGetValue(key, out var id))
                {
                    if (!ids.Contains(id))
                    {
                        problems.Add($"{key} maps to missing palette command '{id}'");
                    }
                }
                else if (!CommandsOutsideThePalette.ContainsKey(key))
                {
                    problems.Add($"{key} is neither in the palette nor classified as row-level (add it to AppCommands, or to CommandsOutsideThePalette with the reason)");
                }
            }
        }

        foreach (var stale in CoveredCommands.Keys.Concat(CommandsOutsideThePalette.Keys).Where(k => !found.Contains(k)))
        {
            problems.Add($"{stale} no longer exists; update CommandPaletteTests");
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Palette_commands_are_enabled_by_state_and_unavailable_ones_do_not_run()
    {
        var (window, shell) = await ShowAsync();
        var commands = _host.Get<AppCommands>().Build(shell);
        AppCommand Command(string id) => commands.Single(c => c.Id == id);

        // No register on screen: register actions are listed but unavailable.
        Command("reconcile").IsEnabled.ShouldBeFalse();
        Command("record-balance").IsEnabled.ShouldBeFalse();
        Command("reconcile").AutomationName.ShouldContain("unavailable");
        Command("backup").IsEnabled.ShouldBeTrue();
        Command("encrypt-file").IsEnabled.ShouldBeTrue();
        Command("remove-encryption").IsEnabled.ShouldBeFalse();
        Command("unlock-file").IsEnabled.ShouldBeFalse();
        Command("undo").IsEnabled.ShouldBeFalse();

        // In an account register they become available.
        var account = await Task.Run(() => _host.Get<IAccountService>().CreateAccountAsync(new CreateAccountRequest("Joint checking", AccountType.Checking, "USD", new DateOnly(2026, 1, 1), 0), CancellationToken.None));
        await UiTestHelpers.WaitUntilAsync(() => shell.AccountGroups.SelectMany(g => g.Accounts).Any(), "account in the sidebar");
        shell.OpenAccount(account.Id);
        await _host.Get<AccountsViewModel>().SettleAsync();
        commands = _host.Get<AppCommands>().Build(shell);
        Command("reconcile").IsEnabled.ShouldBeTrue();
        Command("edit-account").IsEnabled.ShouldBeTrue();
        Command("clear-filters").IsEnabled.ShouldBeTrue();
        Command("record-balance").IsEnabled.ShouldBeFalse("only tracking accounts record balances");
        Command("undo").IsEnabled.ShouldBeTrue();

        // The palette keeps an unavailable command listed but does not run it.
        var run = shell.OpenCommandPaletteAsync();
        var palette = await ImportDialogTests.DialogAsync<CommandPaletteViewModel>(shell);
        palette.Query = "record a balance";
        palette.Selected!.Id.ShouldBe("record-balance");
        palette.ConfirmCommand.Execute(null);
        palette.Error.ShouldBe(Keel.Desktop.Resources.Strings.Palette_UnavailableError);
        shell.Dialogs.Current.ShouldBeSameAs(palette);

        // An available page action runs after the palette closes: Bills tab 3 via the palette.
        palette.Query = "bills subscriptions";
        palette.Selected!.Id.ShouldBe("bills-tabs-3");
        palette.Selected.Keys.ShouldBe("3");
        palette.ConfirmCommand.Execute(null);
        await run;
        shell.CurrentPage.ShouldBeOfType<BillsViewModel>().SelectedTab.ShouldBe(BillsTab.Subscriptions);

        // Reports and Budget page actions.
        commands = _host.Get<AppCommands>().Build(shell);
        Command("reports-pick-3").Execute();
        _host.Get<ReportsViewModel>().SelectedReport!.Kind.ShouldBe(Keel.Desktop.ViewModels.Reports.ReportKind.NetWorth);
        var budget = _host.Get<BudgetViewModel>();
        Command("budget-next").Execute();
        shell.CurrentPage.ShouldBeSameAs(budget);
        await budget.SettleAsync();
        var next = budget.CurrentMonth;
        Command("budget-this-month").Execute();
        await budget.SettleAsync();
        budget.CurrentMonth.ShouldBe(next.AddMonths(-1));
        window.Close();
    }

    private static List<string> Ids(IEnumerable<MenuNode> nodes) =>
        nodes.SelectMany(n => n.Children is { } children ? Ids(children) : n.Command is { } c ? [c.Id] : []).ToList();

    private static MenuNode Leaf(IEnumerable<MenuNode> nodes, string id) =>
        nodes.SelectMany(n => n.Children ?? [n]).SelectMany(n => n.Children ?? [n]).First(n => n.Command?.Id == id);
}
