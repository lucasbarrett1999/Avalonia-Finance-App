using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Keel.Application.Forecast;
using Keel.Application.Payees;
using Keel.Application.Recurring;
using Keel.Application.Scheduling;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Bills;
using Keel.Desktop.ViewModels.Reports;
using Keel.Desktop.Views;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Desktop.Tests;

/// <summary>Renders the M5 screens over fixture data in light and dark (Bills tabs, dialogs, bell panel, forecast, Home cards, ghost rows).</summary>
public sealed class M5RenderingTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

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

    private static async Task CaptureAsync(Window window, string name)
    {
        // LiveCharts redraws through its own throttled loop; give it a moment.
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(60);
        }

        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame()!;
        frame.PixelSize.Width.ShouldBeGreaterThan(0);
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
            frame.Save(Path.Combine(outputDir, name + ".png"));
        }
    }

    [AvaloniaFact]
    public async Task Bills_schedules_alerts_forecast_and_home_render_fixture_data_in_light_and_dark()
    {
        var fixture = await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(
            _host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(5_000, Seed: 42, EndDate: Today), Ct));
        var checking = fixture.Accounts["Everyday Checking"];
        await Task.Run(async () =>
        {
            var payees = _host.Get<IPayeeService>();
            var scheduled = _host.Get<IScheduledTransactionService>();
            var water = await payees.GetOrCreateAsync("City Water", Ct);
            await scheduled.CreateAsync(new ScheduledTransactionEdit(checking, water.Id, -64_00, fixture.Categories["Water"], null, "quarterly meter", "FREQ=MONTHLY", Today.AddDays(3), null, false), Ct);
            var savings = await payees.GetOrCreateAsync("Savings plan", Ct);
            await scheduled.CreateAsync(new ScheduledTransactionEdit(checking, savings.Id, -250_00, null, fixture.Accounts["Emergency Savings"], null, "FREQ=WEEKLY;INTERVAL=2", Today.AddDays(5), null, true), Ct);
            var gym = await payees.GetOrCreateAsync("Iron Gym", Ct);
            await scheduled.CreateAsync(new ScheduledTransactionEdit(checking, gym.Id, -39_00, fixture.Categories["Fitness"], null, null, "FREQ=MONTHLY", Today.AddDays(-2), null, false), Ct);
            await _host.Get<IForecastService>().SaveSettingsAsync(new ForecastSettings(2_000_00, false), Ct);
        });

        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        await SettleAsync(() => shell.Jobs!.Running);
        var themes = _host.Get<ThemeService>();

        // The startup prompt for the overdue gym payment.
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is ScheduledPromptViewModel, "startup prompt");
        themes.SetTheme(AppTheme.Light);
        await CaptureAsync(window, "M5-SchedulePrompt-Light");
        themes.SetTheme(AppTheme.Dark);
        await CaptureAsync(window, "M5-SchedulePrompt-Dark");
        shell.Dialogs.Current!.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Confirm some detections and designate a subscription group so every tab has content.
        var recurring = _host.Get<IRecurringService>();
        await Task.Run(async () =>
        {
            var items = await recurring.GetItemsAsync(new RecurringItemFilter([]), Ct);
            foreach (var item in items.Where(i => i.ExpectedAmount.Amount < 0).Take(4))
            {
                await recurring.ConfirmAsync(item.Id, Ct);
            }

            var groups = (await _host.Get<Keel.Application.Categories.ICategoryService>().GetCategoriesAsync(false, Ct))
                .Where(c => c.Name == "Subscriptions").Select(c => c.GroupId).Distinct().ToList();
            await recurring.SetSubscriptionDesignationsAsync(new SubscriptionDesignations(groups, []), Ct);
            foreach (var item in (await recurring.GetItemsAsync(new RecurringItemFilter([]), Ct)).Where(i => i.CategoryName is "Streaming" or "Internet"))
            {
                await recurring.UpdateAsync(item.Id, new RecurringItemEdit(item.PayeeId, item.AccountId, item.Cadence, item.ExpectedAmount.Amount, item.NextExpectedDate, item.CategoryId, true), Ct);
            }
        });

        var bills = _host.Get<BillsViewModel>();
        var reports = _host.Get<ReportsViewModel>();
        var home = _host.Get<HomeViewModel>();
        var register = _host.Get<AccountsViewModel>();

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);

            shell.PrimaryItems.Single(i => i.PageType == typeof(HomeViewModel)).NavigateCommand.Execute(null);
            await SettleAsync(() => home.Loading);
            home.HasForecast.ShouldBeTrue();
            await CaptureAsync(window, $"M5-Home-{theme}");
            window.Named<ScrollViewer>("DashboardScroll").ScrollToEnd();
            await CaptureAsync(window, $"M5-HomeLower-{theme}");
            window.Named<ScrollViewer>("DashboardScroll").ScrollToHome();

            shell.PrimaryItems.Single(i => i.PageType == typeof(BillsViewModel)).NavigateCommand.Execute(null);
            await SettleAsync(() => bills.Loading);
            bills.ShowContent.ShouldBeTrue();
            bills.SelectedTab = BillsTab.Calendar;
            await CaptureAsync(window, $"M5-Bills-Calendar-{theme}");
            bills.SelectedTab = BillsTab.List;
            await bills.SelectItemCommand.ExecuteAsync(bills.ListItems.First(i => !i.IsIncome && i.IsActive));
            await CaptureAsync(window, $"M5-Bills-List-{theme}");
            bills.SelectedTab = BillsTab.Subscriptions;
            await CaptureAsync(window, $"M5-Bills-Subscriptions-{theme}");
            bills.CloseDetailCommand.Execute(null);

            shell.OpenAccount(checking);
            await register.SettleAsync();
            await SettleAsync(() => register.Scheduled!.Loading);
            register.Scheduled!.HasRows.ShouldBeTrue();
            await CaptureAsync(window, $"M5-RegisterGhostRows-{theme}");

            _ = register.Scheduled.EditCommand.ExecuteAsync(register.Scheduled.Rows.First(r => r.Payee == "City Water"));
            await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is ScheduledTransactionEditorViewModel, "schedule dialog");
            await CaptureAsync(window, $"M5-ScheduleDialog-{theme}");
            shell.Dialogs.Current!.CancelCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            shell.Notifications!.ToggleCommand.Execute(null);
            await SettleAsync(() => shell.Notifications.Loading);
            await CaptureAsync(window, $"M5-Notifications-{theme}");
            shell.Notifications.CloseCommand.Execute(null);

            shell.PrimaryItems.Single(i => i.PageType == typeof(ReportsViewModel)).NavigateCommand.Execute(null);
            await SettleAsync(() => reports.Loading);
            reports.SelectedReport = reports.Reports.Single(r => r.Kind == ReportKind.Forecast);
            await SettleAsync(() => reports.Loading);
            var forecast = (ForecastReportViewModel)reports.SelectedReport;
            forecast.HasData.ShouldBeTrue();
            forecast.ExplainLowestCommand.Execute(forecast.Series[0]);
            await forecast.Explaining;
            await CaptureAsync(window, $"M5-Reports-Forecast-{theme}");
            window.Named<ScrollViewer>("ForecastScroll").ScrollToEnd();
            await CaptureAsync(window, $"M5-Reports-ForecastLower-{theme}");
            window.Named<ScrollViewer>("ForecastScroll").ScrollToHome();
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task Bills_empty_state_before_detection_renders_in_light_and_dark()
    {
        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        var themes = _host.Get<ThemeService>();
        var bills = _host.Get<BillsViewModel>();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            shell.PrimaryItems.Single(i => i.PageType == typeof(BillsViewModel)).NavigateCommand.Execute(null);
            await SettleAsync(() => bills.Loading);
            bills.ShowAnyEmptyState.ShouldBeTrue();
            await CaptureAsync(window, $"M5-Bills-Empty-{theme}");
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }
}
