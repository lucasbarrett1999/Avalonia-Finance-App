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

    private static List<string> Ids(IEnumerable<MenuNode> nodes) =>
        nodes.SelectMany(n => n.Children is { } children ? Ids(children) : n.Command is { } c ? [c.Id] : []).ToList();

    private static MenuNode Leaf(IEnumerable<MenuNode> nodes, string id) =>
        nodes.SelectMany(n => n.Children ?? [n]).SelectMany(n => n.Children ?? [n]).First(n => n.Command?.Id == id);
}
