using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.Views;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

using Xunit.Abstractions;

namespace Keel.Desktop.Tests;

/// <summary>The accessibility pass (PRD 8, 9.11, 11) over every screen with data, in both themes.</summary>
public sealed class AccessibilityTests(ITestOutputHelper output) : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Every_screen_names_its_controls_and_meets_AA_text_contrast_in_both_themes()
    {
        await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(_host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(1_500, Seed: 11, EndDate: DateOnly.FromDateTime(DateTime.Today)), CancellationToken.None));
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = _host.Get<ThemeService>();
        var problems = new List<string>();

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            foreach (var item in shell.AllItems)
            {
                item.NavigateCommand.Execute(null);
                await SettleAsync();
                var screen = $"{item.PageType.Name}/{theme}";
                problems.AddRange(AccessibilityAudit.UnnamedControls(window, screen));
                problems.AddRange(AccessibilityAudit.LowContrastText(window, screen));
                problems.AddRange(AccessibilityAudit.AmountsWithoutTabularFigures(window, screen));
            }
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        foreach (var problem in problems.Distinct())
        {
            output.WriteLine(problem);
        }

        problems.Distinct().ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task Dialogs_tabs_reports_and_the_palette_are_named_and_readable_in_both_themes()
    {
        await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(_host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(800, Seed: 5, EndDate: DateOnly.FromDateTime(DateTime.Today)), CancellationToken.None));
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = _host.Get<ThemeService>();
        var problems = new List<string>();

        async Task AuditAsync(string screen)
        {
            await SettleAsync();
            problems.AddRange(AccessibilityAudit.UnnamedControls(window, screen));
            problems.AddRange(AccessibilityAudit.LowContrastText(window, screen));
            problems.AddRange(AccessibilityAudit.AmountsWithoutTabularFigures(window, screen));
        }

        async Task DialogAsync(string screen, Func<Task> open)
        {
            var opening = open();
            await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is not null, screen + " shown");
            await AuditAsync(screen);
            shell.Dialogs.Current!.CancelCommand.Execute(null);
            await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is null, screen + " closed");
            await opening;
        }

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            await DialogAsync($"Palette/{theme}", shell.OpenCommandPaletteAsync);
            await DialogAsync($"AddAccount/{theme}", () => { shell.AddAccountCommand.Execute(null); return Task.CompletedTask; });
            await DialogAsync($"About/{theme}", () => { _host.Get<AppCommands>().Build(shell).Single(c => c.Id == "about").Execute(); return Task.CompletedTask; });

            shell.NavigateTo<BudgetViewModel>();
            await _host.Get<BudgetViewModel>().SettleAsync();
            await DialogAsync($"MoveMoney/{theme}", () => _host.Get<BudgetViewModel>().MoveMoneyAsync());

            shell.NavigateTo<GoalsViewModel>();
            await SettleAsync();
            await DialogAsync($"NewGoal/{theme}", () => _host.Get<GoalsViewModel>().NewGoalAsync());

            shell.NavigateTo<BillsViewModel>();
            var bills = _host.Get<BillsViewModel>();
            foreach (var tab in Enum.GetValues<BillsTab>())
            {
                bills.SelectedTab = tab;
                await AuditAsync($"Bills.{tab}/{theme}");
            }

            await DialogAsync($"NewBill/{theme}", bills.AddItemAsync);

            shell.NavigateTo<ReportsViewModel>();
            var reports = _host.Get<ReportsViewModel>();
            foreach (var report in reports.Reports)
            {
                reports.SelectedReport = report;
                await AuditAsync($"Report.{report.GetType().Name}/{theme}");
            }

            shell.AccountItems[0].NavigateCommand.Execute(null);
            var register = _host.Get<AccountsViewModel>();
            await register.SettleAsync();
            await register.NewTransactionAsync();
            await AuditAsync($"RegisterEditor/{theme}");
            register.CancelEdit();

            shell.Notifications!.ToggleCommand.Execute(null);
            await AuditAsync($"Notifications/{theme}");
            shell.Notifications.Close();

            var connections = _host.Get<Keel.Desktop.ViewModels.Sync.ConnectionsSettingsViewModel>();
            shell.NavigateToSettings("Connections");
            await DialogAsync($"AddConnection/{theme}", () => connections.Coordinator.AddConnectionAsync());
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        foreach (var problem in problems.Distinct())
        {
            output.WriteLine(problem);
        }

        problems.Distinct().ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task Home_and_goals_show_a_loading_state_until_their_first_load()
    {
        var home = _host.Get<HomeViewModel>();
        var goals = _host.Get<GoalsViewModel>();
        home.ShowLoading.ShouldBeTrue();
        goals.ShowLoading.ShouldBeTrue();
        home.ShowDashboard.ShouldBeFalse();
        home.ShowEmptyState.ShouldBeFalse();

        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        shell.NavigateTo<HomeViewModel>();
        await UiTestHelpers.WaitUntilAsync(() => home.IsInitialized, "home loaded");
        home.ShowLoading.ShouldBeFalse();
        home.ShowEmptyState.ShouldBeTrue();
        shell.NavigateTo<GoalsViewModel>();
        await UiTestHelpers.WaitUntilAsync(() => goals.IsInitialized, "goals loaded");
        goals.ShowLoading.ShouldBeFalse();
        goals.ShowEmptyState.ShouldBeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_first_run_steps_are_named_and_readable_in_both_themes()
    {
        using var host = TestHost.CreateFirstRun();
        var window = host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        var themes = host.Get<ThemeService>();
        var problems = new List<string>();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            await SettleAsync();
            problems.AddRange(AccessibilityAudit.UnnamedControls(window, $"Welcome/{theme}"));
            problems.AddRange(AccessibilityAudit.LowContrastText(window, $"Welcome/{theme}"));
        }

        await shell.FirstRun!.CreateAsync();
        await UiTestHelpers.WaitUntilAsync(() => !ReferenceEquals(window.DataContext, shell), "switched");
        var setup = ((ShellViewModel)window.DataContext!).FirstRun!;
        foreach (var step in new[] { "Template", "Account" })
        {
            if (step == "Account")
            {
                await setup.ApplyTemplateAsync();
                setup.ToggleBankInfo();
            }

            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                themes.SetTheme(theme);
                await SettleAsync();
                problems.AddRange(AccessibilityAudit.UnnamedControls(window, $"{step}/{theme}"));
                problems.AddRange(AccessibilityAudit.LowContrastText(window, $"{step}/{theme}"));
            }
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        foreach (var problem in problems.Distinct())
        {
            output.WriteLine(problem);
        }

        problems.Distinct().ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task Debt_payoff_budget_health_and_debt_terms_are_named_and_readable_in_both_themes()
    {
        var fixture = await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(_host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(1_200, Seed: 9, EndDate: DateOnly.FromDateTime(DateTime.Today)), CancellationToken.None));
        await DebtHealthRenderingTests.AddDebtTermsAsync(_host, fixture);
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = _host.Get<ThemeService>();
        var goals = _host.Get<GoalsViewModel>();
        var problems = new List<string>();

        void Audit(string screen)
        {
            problems.AddRange(AccessibilityAudit.UnnamedControls(window, screen));
            problems.AddRange(AccessibilityAudit.LowContrastText(window, screen));
            problems.AddRange(AccessibilityAudit.AmountsWithoutTabularFigures(window, screen));
        }

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            shell.NavigateTo<GoalsViewModel>();
            goals.SelectedTab = GoalsTab.DebtPayoff;
            await UiTestHelpers.WaitUntilAsync(() => goals.DebtPayoff!.IsInitialized, "debt plan loaded");
            await SettleAsync();
            goals.DebtPayoff!.Rows.ShouldNotBeEmpty();
            Audit($"DebtPayoff/{theme}");

            var editing = goals.DebtPayoff.EditAccountCommand.ExecuteAsync(goals.DebtPayoff.Missing[0]);
            await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is not null, "editor shown");
            await SettleAsync();
            Audit($"AccountEditor.DebtTerms/{theme}");
            shell.Dialogs.Current!.CancelCommand.Execute(null);
            await editing;
            goals.SelectedTab = GoalsTab.Goals;

            shell.NavigateTo<ReportsViewModel>();
            var reports = _host.Get<ReportsViewModel>();
            reports.SelectedReport = reports.Reports.Single(r => r.Kind == Keel.Desktop.ViewModels.Reports.ReportKind.BudgetHealth);
            await UiTestHelpers.WaitUntilAsync(() => reports.Loading.IsCompleted, "health loaded");
            await SettleAsync();
            Audit($"BudgetHealth/{theme}");
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        foreach (var problem in problems.Distinct())
        {
            output.WriteLine(problem);
        }

        problems.Distinct().ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task Export_bundle_and_migration_dialogs_are_named_and_readable_in_both_themes()
    {
        await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(_host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(300, Seed: 8, EndDate: DateOnly.FromDateTime(DateTime.Today)), CancellationToken.None));
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = _host.Get<ThemeService>();
        var problems = new List<string>();
        var scenes = await PortabilityScenes.DialogsAsync(_host, shell, Path.Combine(_host.Root, "exports"));
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            foreach (var (name, open) in scenes)
            {
                var showing = open();
                await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is not null, name + " shown");
                await SettleAsync();
                problems.AddRange(AccessibilityAudit.UnnamedControls(window, $"{name}/{theme}"));
                problems.AddRange(AccessibilityAudit.LowContrastText(window, $"{name}/{theme}"));
                problems.AddRange(AccessibilityAudit.AmountsWithoutTabularFigures(window, $"{name}/{theme}"));
                shell.Dialogs.Current!.CancelCommand.Execute(null);
                await showing;
            }
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        foreach (var problem in problems.Distinct())
        {
            output.WriteLine(problem);
        }

        problems.Distinct().ShouldBeEmpty();
    }
}
