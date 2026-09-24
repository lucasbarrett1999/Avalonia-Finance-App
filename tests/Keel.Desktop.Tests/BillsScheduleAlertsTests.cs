using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Application.Payees;
using Keel.Application.Recurring;
using Keel.Application.Scheduling;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Bills;
using Keel.Desktop.ViewModels.Reports;
using Keel.Desktop.Views;
using Keel.Domain;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Desktop.Tests;

/// <summary>Builds recurring series through the real services, relative to today.</summary>
internal sealed class M5Data(TestHost host)
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private static CancellationToken Ct => CancellationToken.None;

    public Task<AccountDto> AccountAsync(string name, AccountType type = AccountType.Checking, long opening = 3_000_00) =>
        Task.Run(() => host.Get<IAccountService>().CreateAccountAsync(new CreateAccountRequest(name, type, "USD", Today.AddYears(-2), opening), Ct));

    public Task<Guid> CategoryAsync(string group, string name) =>
        Task.Run(async () => (await host.Get<ICategoryService>().CreateCategoryAsync(group, name, Ct)).Id);

    public Task MonthlyAsync(Guid account, string payee, long amount, int count, DateOnly last, Guid? category = null) =>
        Task.Run(async () =>
        {
            for (var i = count - 1; i >= 0; i--)
            {
                await host.Get<ITransactionService>().SaveAsync(
                    new SaveTransactionRequest(null, account, last.AddMonths(-i), amount, payee, category, null, TransactionStatus.Cleared), Ct);
            }
        });

    public Task EveryAsync(Guid account, string payee, long amount, int count, int days, DateOnly last) =>
        Task.Run(async () =>
        {
            for (var i = count - 1; i >= 0; i--)
            {
                await host.Get<ITransactionService>().SaveAsync(
                    new SaveTransactionRequest(null, account, last.AddDays(-i * days), amount, payee, null, null, TransactionStatus.Cleared), Ct);
            }
        });

    /// <summary>Checking with rent, streaming, a gym and a biweekly paycheck; returns the checking account.</summary>
    public async Task<AccountDto> HouseholdAsync()
    {
        var checking = await AccountAsync("Everyday Checking");
        var streaming = await CategoryAsync("Subscriptions", "Streaming");
        var rent = await CategoryAsync("Bills", "Rent");
        await MonthlyAsync(checking.Id, "Netflix", -15_49, 6, Today.AddDays(-4), streaming);
        await MonthlyAsync(checking.Id, "Riverside Apartments", -1_450_00, 6, Today.AddDays(-20), rent);
        await MonthlyAsync(checking.Id, "Iron Gym", -39_00, 5, Today.AddDays(-9));
        await EveryAsync(checking.Id, "Acme Payroll", 1_900_00, 8, 14, Today.AddDays(-6));
        return checking;
    }
}

/// <summary>Headless flows for Bills, scheduled transactions, the notification center and the forecast report (M5).</summary>
public sealed class BillsScheduleAlertsTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    private static DateOnly Today => M5Data.Today;

    private static async Task SettleAsync(Func<Task> loading)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await loading();
            await Task.Delay(15);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private async Task<(ShellWindow Window, ShellViewModel Shell)> ShowAsync()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        await SettleAsync(() => shell.Jobs!.Running);
        return (window, shell);
    }

    private async Task<BillsViewModel> OpenBillsAsync(ShellViewModel shell)
    {
        shell.PrimaryItems.Single(i => i.PageType == typeof(BillsViewModel)).NavigateCommand.Execute(null);
        var bills = _host.Get<BillsViewModel>();
        await SettleAsync(() => bills.Loading);
        return bills;
    }

    [AvaloniaFact]
    public async Task Bills_shows_its_empty_state_until_detection_runs_then_lists_confirms_and_totals_items()
    {
        await new M5Data(_host).HouseholdAsync();
        var bills = _host.Get<BillsViewModel>();
        bills.OnNavigatedTo(null);
        await SettleAsync(() => bills.Loading);
        bills.ShowEmptyState.ShouldBeTrue("detection has never run");
        bills.EmptyHeading.ShouldBe(Keel.Desktop.Resources.Strings.Page_Bills_EmptyHeading);

        await bills.RunDetectionCommand.ExecuteAsync(null);
        await SettleAsync(() => bills.Loading);
        bills.ShowContent.ShouldBeTrue();
        bills.LastDetection.ShouldBe(Today);
        bills.ListItems.Select(i => i.PayeeName).ShouldBe(["Acme Payroll", "Riverside Apartments", "Iron Gym", "Netflix"], ignoreOrder: true);
        bills.ListItems.ShouldAllBe(i => i.IsDetected);
        bills.DetectedCount.ShouldBe(4);
        bills.MonthlyOutflowText.ShouldBe(Services.LedgerText.Money(15_49 + 1_450_00 + 39_00, "USD"));

        // Calendar: this month's grid has the expected charges on their days.
        bills.SelectedTab = BillsTab.Calendar;
        bills.Days.Count.ShouldBe(42);
        var netflix = bills.ListItems.Single(i => i.PayeeName == "Netflix");
        var calendarEntries = bills.Days.SelectMany(d => d.Entries).Where(e => e.ItemId == netflix.Id).ToList();
        calendarEntries.ShouldNotBeEmpty();
        await bills.NextMonthCommand.ExecuteAsync(null);
        bills.Days.SelectMany(d => d.Entries).ShouldContain(e => e.ItemId == netflix.Id && !e.IsPaid);

        // Confirm from the detail panel.
        await bills.SelectItemCommand.ExecuteAsync(netflix);
        bills.Detail.ShouldNotBeNull();
        bills.Detail.CanConfirm.ShouldBeTrue();
        bills.Detail.History.Count.ShouldBe(6);
        bills.Detail.Alerts.ShouldHaveSingleItem().Kind.ShouldBe(AlertKind.NewRecurring);
        await bills.ConfirmCommand.ExecuteAsync(null);
        await SettleAsync(() => bills.Loading);
        bills.Detail!.Item.Status.ShouldBe(RecurringStatus.Active);
        bills.DetectedCount.ShouldBe(3);
        bills.SelectedFilter = bills.Filters.Single(f => f.Value == BillsFilter.Active);
        bills.ListItems.Select(i => i.PayeeName).ShouldBe(["Netflix"]);

        // Subscriptions: designate the group; Netflix becomes a subscription with monthly and yearly totals.
        bills.SelectedTab = BillsTab.Subscriptions;
        bills.HasSubscriptions.ShouldBeFalse();
        bills.SubscriptionGroups.Single(g => g.Name == "Subscriptions").IsChecked = true;
        await UiTestHelpers.WaitUntilAsync(() => bills.Subscriptions.Count == 1, "Netflix classified as a subscription");
        bills.SubscriptionsMonthlyText.ShouldBe(Services.LedgerText.Money(15_49, "USD"));
        bills.SubscriptionsYearlyText.ShouldBe(Services.LedgerText.Money(185_88, "USD"));

        // F-REC-4 target from the item.
        await bills.CreateTargetCommand.ExecuteAsync(null);
        (await _host.Get<Keel.Application.Budget.IBudgetService>().GetTargetAsync(bills.Detail!.Item.CategoryId!.Value, Ct))!.Amount.ShouldBe(15_49);
    }

    [AvaloniaFact]
    public async Task A_schedule_built_in_the_dialog_shows_as_a_ghost_row_and_is_entered_from_it()
    {
        var checking = await new M5Data(_host).AccountAsync("Everyday Checking");
        var (window, shell) = await ShowAsync();
        shell.OpenAccount(checking.Id);
        var register = _host.Get<AccountsViewModel>();
        await register.SettleAsync();
        register.Scheduled.ShouldNotBeNull();
        register.Scheduled.HasRows.ShouldBeFalse();

        window.Named<Button>("ScheduleTransactionButton").Command!.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is ScheduledTransactionEditorViewModel, "schedule dialog");
        var dialog = (ScheduledTransactionEditorViewModel)shell.Dialogs.Current!;
        dialog.Account!.Id.ShouldBe(checking.Id);
        dialog.Payee = "City Power";
        dialog.Amount = 84_00;
        dialog.Frequency = dialog.Frequencies.Single(f => f.Value == ScheduleFrequency.Weekly);
        dialog.Interval = 2;
        foreach (var toggle in dialog.WeekdayToggles)
        {
            toggle.IsChecked = toggle.Day == Today.DayOfWeek;
        }

        dialog.StartDate = Today.ToDateTime(TimeOnly.MinValue);
        dialog.End = dialog.Ends.Single(e => e.Value == ScheduleEnd.AfterCount);
        dialog.Count = 6;
        Dispatcher.UIThread.RunJobs();
        dialog.HasRuleError.ShouldBeFalse();
        dialog.Description.ShouldStartWith("Every 2 weeks on " + Today.DayOfWeek);
        dialog.Description.ShouldEndWith("6 times");
        dialog.NextDates.Count.ShouldBe(ScheduledTransactionEditorViewModel.PreviewCount);
        window.FindNamed<TextBlock>("RuleDescription")!.Text.ShouldBe(dialog.Description);
        await dialog.ConfirmCommand.ExecuteAsync(null);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is null, "dialog closed");

        await UiTestHelpers.WaitUntilAsync(() => register.Scheduled.Rows.Count == 3, "ghost rows for today, +14 and +28 days");
        var ghosts = window.Named<ItemsControl>("GhostRows");
        ghosts.IsEffectivelyVisible.ShouldBeTrue();
        var first = register.Scheduled.Rows[0];
        first.Date.ShouldBe(Today);
        first.IsNext.ShouldBeTrue();
        register.Scheduled.Rows.Skip(1).ShouldAllBe(r => !r.IsNext);

        var before = register.RowCount;
        await register.Scheduled.EnterCommand.ExecuteAsync(first);
        await register.SettleAsync();
        await UiTestHelpers.WaitUntilAsync(() => register.RowCount == before + 1, "entered into the register");
        await UiTestHelpers.WaitUntilAsync(() => register.Scheduled.Rows.Count == 2 && register.Scheduled.Rows[0].Date == Today.AddDays(14), "ghost moved on");
        var db = _host.Get<IDbContextFactory<KeelDbContext>>().CreateDbContext();
        await using (db)
        {
            var entered = await db.Transactions.SingleAsync(t => t.ScheduledFromId != null);
            entered.Amount.ShouldBe(-84_00);
            entered.Source.ShouldBe(TransactionSource.Scheduled);
        }

        // Skip the next one; undo brings it back.
        await register.Scheduled.SkipCommand.ExecuteAsync(register.Scheduled.Rows[0]);
        await UiTestHelpers.WaitUntilAsync(() => register.Scheduled.Rows.Count == 1, "skipped");
        await shell.UndoCommand.ExecuteAsync(null);
        await UiTestHelpers.WaitUntilAsync(() => register.Scheduled.Rows.Count == 2, "undo restored the instance");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Due_prompted_schedules_are_offered_on_start_and_entered_all_at_once()
    {
        var checking = await new M5Data(_host).AccountAsync("Everyday Checking");
        await Task.Run(async () =>
        {
            var payee = await _host.Get<IPayeeService>().GetOrCreateAsync("Water Utility", Ct);
            await _host.Get<IScheduledTransactionService>().CreateAsync(
                new ScheduledTransactionEdit(checking.Id, payee.Id, -45_00, null, null, null, "FREQ=WEEKLY", Today.AddDays(-7), null, false), Ct);
        });
        var (window, shell) = await ShowAsync();
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is ScheduledPromptViewModel, "the startup prompt");
        var prompt = (ScheduledPromptViewModel)shell.Dialogs.Current!;
        prompt.Rows.Select(r => r.Date).ShouldBe([Today.AddDays(-7), Today]);
        window.Named<ItemsControl>("PromptRows").ItemCount.ShouldBe(2);
        await prompt.ConfirmCommand.ExecuteAsync(null);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is null, "prompt closed");
        prompt.EnteredCount.ShouldBe(2);
        var db = _host.Get<IDbContextFactory<KeelDbContext>>().CreateDbContext();
        await using (db)
        {
            (await db.Transactions.CountAsync(t => t.ScheduledFromId != null)).ShouldBe(2);
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task The_bell_shows_unread_alerts_opens_its_panel_dismisses_and_navigates()
    {
        await new M5Data(_host).HouseholdAsync();
        var (window, shell) = await ShowAsync();
        var center = shell.Notifications!;
        await UiTestHelpers.WaitUntilAsync(() => center.UnreadCount == 4, "a new-item alert per detected item");
        var badge = window.Named<Border>("BellBadge");
        badge.IsVisible.ShouldBeTrue();
        center.BadgeText.ShouldBe("4");

        window.Named<Button>("BellButton").Command!.Execute(null);
        await SettleAsync(() => center.Loading);
        center.IsOpen.ShouldBeTrue();
        window.Named<Panel>("NotificationLayer").IsVisible.ShouldBeTrue();
        window.Named<ItemsControl>("AlertList").ItemCount.ShouldBe(4);
        center.Items.ShouldAllBe(i => i.IsUnread && i.Kind == AlertKind.NewRecurring);

        await center.DismissCommand.ExecuteAsync(center.Items[0]);
        await UiTestHelpers.WaitUntilAsync(() => center.Items.Count == 3 && center.UnreadCount == 3, "dismissed");
        await center.MarkAllReadCommand.ExecuteAsync(null);
        await UiTestHelpers.WaitUntilAsync(() => center.UnreadCount == 0 && center.Items.All(i => !i.IsUnread), "all read");
        Dispatcher.UIThread.RunJobs();
        badge.IsVisible.ShouldBeFalse();

        var target = center.Items[0];
        await center.OpenCommand.ExecuteAsync(target);
        center.IsOpen.ShouldBeFalse();
        shell.CurrentPage.ShouldBeOfType<BillsViewModel>();
        var bills = _host.Get<BillsViewModel>();
        await SettleAsync(() => bills.Loading);
        bills.Detail!.Item.Id.ShouldBe(target.Alert.RecurringItemId!.Value);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_forecast_report_lists_days_below_the_floor_and_explains_a_day()
    {
        var checking = (await new M5Data(_host).HouseholdAsync()).Id;
        var (window, shell) = await ShowAsync();
        var recurring = _host.Get<IRecurringService>();
        await Task.Run(async () =>
        {
            foreach (var item in await recurring.GetItemsAsync(new RecurringItemFilter([]), Ct))
            {
                await recurring.ConfirmAsync(item.Id, Ct);
            }
        });

        shell.PrimaryItems.Single(i => i.PageType == typeof(ReportsViewModel)).NavigateCommand.Execute(null);
        var reports = _host.Get<ReportsViewModel>();
        await SettleAsync(() => reports.Loading);
        reports.SelectedReport = reports.Reports.Single(r => r.Kind == ReportKind.Forecast);
        await SettleAsync(() => reports.Loading);
        var forecast = (ForecastReportViewModel)reports.SelectedReport;
        forecast.HasData.ShouldBeTrue();
        forecast.Series.Count.ShouldBe(2, "combined and the checking account");
        forecast.Dates.Count.ShouldBe(91);
        window.Named<ComboBox>("RangeBox").IsVisible.ShouldBeFalse("the forecast has its own horizon");
        forecast.FloorRows.ShouldBeEmpty();

        var balances = forecast.Series.Single(s => s.AccountId == checking).Balances;
        forecast.FloorAmount = balances.Max();
        forecast.UseFloor = true;
        await UiTestHelpers.WaitUntilAsync(() => forecast.HasFloorRows, "days below the floor listed");
        await SettleAsync(() => reports.Loading);
        forecast.FloorRows.ShouldAllBe(r => r.Balance < balances.Max());
        window.Named<ItemsControl>("FloorRowsList").ItemCount.ShouldBe(forecast.FloorRows.Count);
        (await _host.Get<Keel.Application.Forecast.IForecastService>().GetSettingsAsync(Ct)).Floor.ShouldBe(balances.Max());

        var row = forecast.FloorRows[0];
        forecast.ExplainRowCommand.Execute(row);
        await forecast.Explaining;
        forecast.Explanation.ShouldNotBeNull();
        forecast.Explanation.ClosingText.ShouldBe(row.BalanceText);
        Dispatcher.UIThread.RunJobs();
        window.FindNamed<TextBlock>("ExplanationTitle")!.Text!.ShouldContain("Everyday Checking");
        forecast.ToCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(92);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Home_shows_upcoming_bills_and_the_forecast_low_point()
    {
        var data = new M5Data(_host);
        var checking = await data.HouseholdAsync();
        await data.MonthlyAsync(checking.Id, "Phone Co", -60_00, 4, Today.AddDays(3).AddMonths(-1));
        var (window, shell) = await ShowAsync();
        var home = _host.Get<HomeViewModel>();
        shell.PrimaryItems[0].NavigateCommand.Execute(null);
        await SettleAsync(() => home.Loading);
        home.HasForecast.ShouldBeTrue();
        home.ForecastValues.Count.ShouldBe(91);
        home.ForecastLowDetail.ShouldContain("Everyday Checking");
        window.Named<TextBlock>("ForecastLowValue").Text.ShouldBe(home.ForecastLowText);
        home.UpcomingNeedsDetection.ShouldBeFalse();
        home.UpcomingBills.ShouldAllBe(b => b.Date >= Today && b.Date <= Today.AddDays(6));
        var phone = home.UpcomingBills.ShouldHaveSingleItem();
        phone.Payee.ShouldBe("Phone Co");
        phone.Amount.ShouldBe(60_00);
        phone.IsUnconfirmed.ShouldBeTrue();
        window.Named<ItemsControl>("UpcomingBillRows").ItemCount.ShouldBe(1);
        home.UpcomingSummary.ShouldNotBeNullOrWhiteSpace();

        home.OpenForecastCommand.Execute(null);
        shell.CurrentPage.ShouldBeOfType<ReportsViewModel>();
        await SettleAsync(() => _host.Get<ReportsViewModel>().Loading);
        _host.Get<ReportsViewModel>().SelectedReport.Kind.ShouldBe(ReportKind.Forecast);
        home.OpenBillsCommand.Execute(null);
        shell.CurrentPage.ShouldBeOfType<BillsViewModel>();
        window.Close();
    }
}
