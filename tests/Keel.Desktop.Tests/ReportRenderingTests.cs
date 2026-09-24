using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Goals;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Reports;
using Keel.Desktop.Views;
using Keel.Domain.Budgeting;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Desktop.Tests;

/// <summary>Renders Home, every report and Goals over fixture data in light and dark (PRD 13 screenshots).</summary>
public sealed class ReportRenderingTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    private static async Task SettleAsync(Func<Task> loading)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await loading();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Home_reports_and_goals_render_fixture_data_in_light_and_dark()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var month = BudgetMonth.Of(today);
        var fixture = await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(
            _host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(5_000, Seed: 42, EndDate: today), Ct));
        var goals = _host.Get<IGoalService>();
        var budget = _host.Get<IBudgetService>();
        await Task.Run(async () =>
        {
            var vacation = await goals.CreateGoalAsync(new CreateGoalRequest("Summer vacation", 3_600_00, month.AddMonths(8), null, "Goals"), month, Ct);
            var car = await goals.CreateGoalAsync(new CreateGoalRequest("New car", 12_000_00, month.AddMonths(30), fixture.Accounts["Brokerage"], "Goals"), month, Ct);
            var fund = await goals.CreateGoalAsync(new CreateGoalRequest("Emergency cushion", 1_000_00, month.AddMonths(2), null, "Goals"), month, Ct);
            for (var m = -2; m <= 0; m++)
            {
                await budget.AssignAsync(vacation.CategoryId, month.AddMonths(m), 350_00, Ct);
                await budget.AssignAsync(car.CategoryId, month.AddMonths(m), 250_00, Ct);
            }

            await budget.AssignAsync(fund.CategoryId, month, 1_000_00, Ct);
            await budget.AssignAsync(fixture.Categories["Groceries"], month, 600_00, Ct);
        });

        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = _host.Get<ThemeService>();
        var home = _host.Get<HomeViewModel>();
        var reports = _host.Get<ReportsViewModel>();
        var goalsPage = _host.Get<GoalsViewModel>();
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");

        async Task Capture(string name)
        {
            // LiveCharts redraws through its own throttled update loop; give it a moment.
            for (var i = 0; i < 4; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(60);
            }

            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame()!;
            frame.PixelSize.Width.ShouldBeGreaterThan(0);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                frame.Save(Path.Combine(outputDir, name + ".png"));
            }
        }

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);

            shell.PrimaryItems.Single(i => i.PageType == typeof(HomeViewModel)).NavigateCommand.Execute(null);
            await SettleAsync(() => home.Loading);
            home.ShowDashboard.ShouldBeTrue();
            await Capture($"Home-{theme}");
            window.Named<Avalonia.Controls.ScrollViewer>("DashboardScroll").ScrollToEnd();
            await Capture($"HomeLower-{theme}");
            window.Named<Avalonia.Controls.ScrollViewer>("DashboardScroll").ScrollToHome();

            shell.PrimaryItems.Single(i => i.PageType == typeof(ReportsViewModel)).NavigateCommand.Execute(null);
            reports.SelectedRange = reports.Ranges.Single(r => r.Value == ReportRange.LastTwelveMonths);
            foreach (var kind in Enum.GetValues<ReportKind>())
            {
                reports.SelectedReport = reports.Reports.Single(r => r.Kind == kind);
                await SettleAsync(() => reports.Loading);
                reports.SelectedReport.HasData.ShouldBeTrue(kind.ToString());
                if (reports.SelectedReport is SpendingReportViewModel spending)
                {
                    spending.BackCommand.Execute(null);
                    await Capture($"Reports-Spending-{theme}");
                    spending.Open(spending.Items.First(i => i.Name == "Everyday"));
                    await Capture($"Reports-SpendingDrill-{theme}");
                    spending.BackCommand.Execute(null);
                }
                else if (reports.SelectedReport is NetWorthReportViewModel netWorth)
                {
                    netWorth.ShowAccounts = false;
                    await Capture($"Reports-NetWorth-{theme}");
                    netWorth.ShowAccounts = true;
                    await Capture($"Reports-NetWorthAccounts-{theme}");
                    netWorth.ShowAccounts = false;
                }
                else
                {
                    await Capture($"Reports-{kind}-{theme}");
                }
            }

            shell.PrimaryItems.Single(i => i.PageType == typeof(GoalsViewModel)).NavigateCommand.Execute(null);
            await SettleAsync(() => goalsPage.Loading);
            goalsPage.Goals.Count.ShouldBe(3);
            goalsPage.Goals.First(g => g.Name == "New car").ExtraPerMonth = 150;
            await Capture($"Goals-{theme}");

            _ = goalsPage.NewGoalCommand.ExecuteAsync(null);
            await UiTestHelpers.WaitUntilAsync(() => goalsPage.Wizard is not null && shell.Dialogs.Current is not null, "wizard");
            goalsPage.Wizard!.Name = "Home office";
            await Capture($"NewGoalDialog-{theme}");
            shell.Dialogs.Current!.CancelCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }
}
