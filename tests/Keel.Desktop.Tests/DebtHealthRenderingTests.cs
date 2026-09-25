using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Reports;
using Keel.Desktop.Views;
using Keel.Domain;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Desktop.Tests;

/// <summary>
/// Renders the M9b screens in light and dark without binding or resource errors: the Debt payoff tab (with
/// debts and empty), the Budget health report, the Home age-of-money card and the account editor's debt
/// fields. With KEEL_SCREENSHOT_DIR set the frames are saved.
/// </summary>
public sealed class DebtHealthRenderingTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static async Task CaptureAsync(Window window, string name)
    {
        // LiveCharts redraws through its own throttled loop.
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }

        using var frame = window.CaptureRenderedFrame()!;
        frame.PixelSize.Width.ShouldBeGreaterThan(0);
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
            frame.Save(Path.Combine(outputDir, name + ".png"));
        }
    }

    private static async Task SettleAsync(Func<Task> loading)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await loading();
            await Task.Delay(20);
        }
    }

    /// <summary>Rates and minimums for the fixture's debts (the Travel Mastercard is left without, to show the "needs details" list).</summary>
    internal static async Task AddDebtTermsAsync(TestHost host, LedgerFixture fixture)
    {
        var accounts = host.Get<IAccountService>();
        await Task.Run(async () =>
        {
            foreach (var (name, terms) in new[] { ("Visa Rewards", new DebtTerms(2299, 1_300_00)), ("Home Mortgage", new DebtTerms(649, 2_100_00)) })
            {
                var account = (await accounts.GetAccountAsync(fixture.Accounts[name], Ct))!;
                await accounts.UpdateAccountAsync(new UpdateAccountRequest(account.Id, account.Name, account.IsOnBudget, account.Notes, terms), Ct);
            }

            // A small car loan so the plan shows a payoff within a few years next to the mortgage.
            await accounts.CreateAccountAsync(new CreateAccountRequest("Car loan", AccountType.Loan, "USD", DateOnly.FromDateTime(DateTime.Today).AddMonths(-1), -8_400_00, Debt: new DebtTerms(549, 260_00)), Ct);
        });
    }

    [AvaloniaFact]
    public async Task Debt_payoff_budget_health_and_the_age_of_money_card_render_in_light_and_dark()
    {
        using var host = TestHost.Create();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var fixture = await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(5_000, Seed: 42, EndDate: today), Ct));
        await AddDebtTermsAsync(host, fixture);
        await Task.Run(() => host.Get<IBudgetService>().SetTargetAsync(new TargetDto(fixture.Categories["Groceries"], TargetType.MonthlySpending, 700_00), Ct));

        LogCapture.Instance.Clear();
        var window = host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = host.Get<ThemeService>();
        var goals = host.Get<GoalsViewModel>();
        var reports = host.Get<ReportsViewModel>();
        var home = host.Get<HomeViewModel>();

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            shell.NavigateTo<GoalsViewModel>();
            goals.SelectedTab = GoalsTab.DebtPayoff;
            await SettleAsync(() => goals.DebtPayoff!.Loading);
            goals.DebtPayoff!.ExtraPerMonth = 250_00;
            await SettleAsync(() => goals.DebtPayoff.Loading);
            goals.DebtPayoff.Rows.Count.ShouldBe(3);
            goals.DebtPayoff.Missing.Count.ShouldBe(1);
            await CaptureAsync(window, $"Goals-DebtPayoff-{theme}");
            window.GetVisualDescendants().OfType<Views.Goals.DebtPayoffView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
            await CaptureAsync(window, $"Goals-DebtPayoffLower-{theme}");

            var editing = goals.DebtPayoff.EditAccountCommand.ExecuteAsync(goals.DebtPayoff.Missing[0]);
            await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is AccountEditorViewModel, "editor");
            ((AccountEditorViewModel)shell.Dialogs.Current!).ShowDebtTerms.ShouldBeTrue();
            await CaptureAsync(window, $"AccountEditor-DebtTerms-{theme}");
            shell.Dialogs.Current!.CancelCommand.Execute(null);
            await editing;
            goals.SelectedTab = GoalsTab.Goals;

            shell.NavigateTo<ReportsViewModel>();
            reports.SelectedReport = reports.Reports.Single(r => r.Kind == ReportKind.BudgetHealth);
            await SettleAsync(() => reports.Loading);
            reports.SelectedReport.HasData.ShouldBeTrue();
            await CaptureAsync(window, $"Reports-BudgetHealth-{theme}");
            window.GetVisualDescendants().OfType<Views.Reports.BudgetHealthReportView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
            await CaptureAsync(window, $"Reports-BudgetHealthLower-{theme}");

            shell.NavigateTo<HomeViewModel>();
            await SettleAsync(() => home.Loading);
            window.Named<ScrollViewer>("DashboardScroll").ScrollToEnd();
            await CaptureAsync(window, $"Home-AgeOfMoney-{theme}");
            window.Named<ScrollViewer>("DashboardScroll").ScrollToHome();
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task Debt_payoff_empty_state_renders_in_light_and_dark()
    {
        using var host = TestHost.Create();
        await Task.Run(() => host.Get<IAccountService>().CreateAccountAsync(new CreateAccountRequest("Checking", AccountType.Checking, "USD", DateOnly.FromDateTime(DateTime.Today), 1_000_00), Ct));
        LogCapture.Instance.Clear();
        var window = host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var goals = host.Get<GoalsViewModel>();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            host.Get<ThemeService>().SetTheme(theme);
            shell.NavigateTo<GoalsViewModel>();
            goals.SelectedTab = GoalsTab.DebtPayoff;
            await SettleAsync(() => goals.DebtPayoff!.Loading);
            goals.DebtPayoff!.ShowEmptyState.ShouldBeTrue();
            await CaptureAsync(window, $"Goals-DebtPayoffEmpty-{theme}");
        }

        host.Get<ThemeService>().SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }
}
