using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Goals;
using Keel.Application.Ledger;
using Keel.Desktop.Controls;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Goals;
using Keel.Desktop.ViewModels.Reports;
using Keel.Desktop.Views;
using Keel.Desktop.Views.Reports;
using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Domain.Entities;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using LiveChartsCore.SkiaSharpView.Avalonia;
using Microsoft.EntityFrameworkCore;

namespace Keel.Desktop.Tests;

/// <summary>Headless flows for Reports, Goals and the Home dashboard (M6).</summary>
public sealed class ReportsGoalsHomeTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private Task<LedgerFixture> FixtureAsync(int count = 3_000) =>
        Task.Run(() => LedgerFixtureGenerator.GenerateAsync(_host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(count, Seed: 7, EndDate: Today), Ct));

    private async Task<(ShellWindow Window, ShellViewModel Shell)> ShowAsync()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        return (window, shell);
    }

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

    private async Task<ReportsViewModel> OpenReportsAsync(ShellViewModel shell, ReportKind kind)
    {
        shell.PrimaryItems.Single(i => i.PageType == typeof(ReportsViewModel)).NavigateCommand.Execute(null);
        var reports = _host.Get<ReportsViewModel>();
        await SettleAsync(() => reports.Loading);
        reports.SelectedReport = reports.Reports.Single(r => r.Kind == kind);
        await SettleAsync(() => reports.Loading);
        reports.SelectedReport.IsLoaded.ShouldBeTrue();
        return reports;
    }

    private async Task<AccountsViewModel> RegisterAsync(ShellViewModel shell)
    {
        shell.CurrentPage.ShouldBeOfType<AccountsViewModel>();
        var register = _host.Get<AccountsViewModel>();
        await register.SettleAsync();
        return register;
    }

    [AvaloniaFact]
    public async Task Spending_drills_from_group_to_category_to_the_filtered_register()
    {
        var fixture = await FixtureAsync();
        var (window, shell) = await ShowAsync();
        var reports = await OpenReportsAsync(shell, ReportKind.Spending);
        var spending = (SpendingReportViewModel)reports.SelectedReport;
        spending.HasData.ShouldBeTrue();
        spending.Slices.Count.ShouldBeInRange(2, ChartPalette.SlotCount);
        spending.Slices.Select(s => s.Slot).Where(s => s >= 0).ShouldBe(Enumerable.Range(0, spending.Slices.Count(s => s.Slot >= 0)));
        window.GetVisualDescendants().OfType<SpendingReportView>().ShouldHaveSingleItem();
        window.GetVisualDescendants().OfType<PieChart>().Single().Series.Count().ShouldBe(spending.Slices.Count);

        // Group -> its categories (a table row does what its slice does).
        var everyday = spending.Items.First(i => i.Name == "Everyday");
        everyday.OpenCommand!.Execute(everyday);
        spending.CanGoBack.ShouldBeTrue();
        spending.BreadcrumbText.ShouldContain("Everyday");
        spending.Items.ShouldAllBe(i => i.Kind == SpendingItemKind.Category && i.GroupName == "Everyday");

        // Category -> All Accounts register filtered by category and range.
        var groceries = spending.Items.Single(i => i.Name == "Groceries");
        spending.OpenSlice(spending.Slices.ToList().IndexOf(groceries));
        var register = await RegisterAsync(shell);
        register.IsAllAccounts.ShouldBeTrue();
        register.SelectedCategoryFilter.Id.ShouldBe(fixture.Categories["Groceries"]);
        var (from, to) = reports.CurrentRange();
        register.SearchText.ShouldBe($"date:{from:yyyy-MM-dd}..{to:yyyy-MM-dd}");
        register.RowCount.ShouldBeGreaterThan(0);
        register.Rows.LoadedRows.ShouldAllBe(r => r.Date >= from && r.Date <= to);

        // Back in the report the level is kept; Back returns to the groups.
        shell.PrimaryItems.Single(i => i.PageType == typeof(ReportsViewModel)).NavigateCommand.Execute(null);
        await SettleAsync(() => reports.Loading);
        spending.LevelTitle.ShouldBe("Everyday");
        spending.BackCommand.Execute(null);
        spending.CanGoBack.ShouldBeFalse();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_a_donut_slice_drills_down()
    {
        await FixtureAsync();
        var (window, shell) = await ShowAsync();
        var reports = await OpenReportsAsync(shell, ReportKind.Spending);
        var spending = (SpendingReportViewModel)reports.SelectedReport;
        var pie = window.GetVisualDescendants().OfType<PieChart>().Single();
        await UiTestHelpers.WaitUntilAsync(() => pie.Bounds.Width > 0, "pie laid out");
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        // The largest slice starts at 12 o'clock and runs clockwise: click just right of the top, mid-ring.
        var center = new Point(pie.Bounds.Width / 2, pie.Bounds.Height / 2);
        var radius = (Math.Min(pie.Bounds.Width, pie.Bounds.Height) / 2) - 20;
        var target = pie.TranslatePoint(new Point(center.X + (radius * Math.Sin(0.2)), center.Y - (radius * Math.Cos(0.2))), window)!.Value;
        var groups = spending.Slices.Where(s => s.Kind != SpendingItemKind.Other).Select(s => s.Name).ToList();
        window.MouseDown(target, MouseButton.Left);
        window.MouseUp(target, MouseButton.Left);
        await UiTestHelpers.WaitUntilAsync(() => spending.CanGoBack, "slice click drilled into a group");
        groups.ShouldContain(spending.LevelTitle);
        spending.Items.ShouldAllBe(i => i.GroupName == spending.LevelTitle);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Income_vs_expense_bars_open_income_in_the_register_and_expense_in_spending()
    {
        var fixture = await FixtureAsync();
        var (window, shell) = await ShowAsync();
        var reports = await OpenReportsAsync(shell, ReportKind.IncomeExpense);
        var ie = (IncomeExpenseReportViewModel)reports.SelectedReport;
        ie.Rows.Count.ShouldBe(12);
        ie.HasData.ShouldBeTrue();
        window.GetVisualDescendants().OfType<CartesianChart>().Single().Series.Count().ShouldBe(3);
        var index = ie.Rows.ToList().FindLastIndex(r => r.Income > 0 && r.Expense > 0);
        var row = ie.Rows[index];

        ie.OpenPoint(0, index);
        var register = await RegisterAsync(shell);
        register.SelectedCategoryFilter.Id.ShouldBe(SystemIds.ReadyToAssignCategory);
        register.SearchText.ShouldBe($"date:{row.From:yyyy-MM-dd}..{row.To:yyyy-MM-dd}");
        register.RowCount.ShouldBeGreaterThan(0);
        register.Rows.LoadedRows.Sum(r => r.Amount).ShouldBe(row.Income);

        ie.OpenPoint(1, index);
        await SettleAsync(() => reports.Loading);
        reports.SelectedReport.ShouldBeOfType<SpendingReportViewModel>();
        reports.SelectedRange.Value.ShouldBe(ReportRange.Custom);
        reports.CurrentRange().ShouldBe((row.From, row.To));
        ((SpendingReportViewModel)reports.SelectedReport).Report!.Total.ShouldBe(row.Expense);

        reports.SelectedReport = ie;
        await SettleAsync(() => reports.Loading);
        ie.Rows.ShouldHaveSingleItem();
        ie.OpenPoint(2, 0);
        register = await RegisterAsync(shell);
        register.SelectedCategoryFilter.Id.ShouldBeNull();
        register.RowCount.ShouldBeGreaterThan(0);
        fixture.TransactionCount.ShouldBeGreaterThan(register.RowCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Net_worth_points_and_account_bands_open_the_register()
    {
        var fixture = await FixtureAsync();
        var (window, shell) = await ShowAsync();
        var reports = await OpenReportsAsync(shell, ReportKind.NetWorth);
        var nw = (NetWorthReportViewModel)reports.SelectedReport;
        nw.SupportsTransfers.ShouldBeFalse();
        nw.IncludeTracking.ShouldBeTrue();
        var months = BudgetMonth.Between(fixture.FirstDate, Today) + 1;
        nw.Rows.Count.ShouldBe(months);             // no points before the accounts were opened
        nw.Rows[0].Date.ShouldBe(fixture.FirstDate.AddMonths(1).AddDays(-fixture.FirstDate.AddMonths(1).Day));
        nw.Series.Count.ShouldBe(8);
        nw.Rows[^1].NetWorth.ShouldBe(nw.Report!.Accounts.Sum(a => a.Balances[^1]));
        window.Named<CheckBox>("IncludeTransfersBox").IsVisible.ShouldBeFalse();

        nw.ShowAccounts = true;
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<CartesianChart>().Single().Series.Count().ShouldBe(9);

        var last = nw.Rows.Count - 1;
        nw.OpenPoint(-1, last);
        var register = await RegisterAsync(shell);
        register.IsAllAccounts.ShouldBeTrue();
        register.SearchText.ShouldBe($"date:{BudgetMonth.Of(nw.Rows[last].Date):yyyy-MM-dd}..{nw.Rows[last].Date:yyyy-MM-dd}");

        var visa = nw.Series.Single(s => s.Name == "Visa Rewards");
        nw.OpenPoint(nw.Series.ToList().IndexOf(visa), last);
        register = await RegisterAsync(shell);
        register.AccountId.ShouldBe(fixture.Accounts["Visa Rewards"]);
        register.RowCount.ShouldBeGreaterThan(0);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Report_toggles_and_account_filter_reload_the_report()
    {
        var fixture = await FixtureAsync();
        var (window, _) = await ShowAsync();
        var shell = (ShellViewModel)window.DataContext!;
        var reports = await OpenReportsAsync(shell, ReportKind.Spending);
        var spending = (SpendingReportViewModel)reports.SelectedReport;
        var total = spending.Report!.Total;

        spending.IncludeTransfers = false;
        await SettleAsync(() => reports.Loading);
        spending.Report!.Total.ShouldBeLessThan(total); // on-budget to tracking transfers are categorized in the fixture

        reports.AccountOptions.Single(a => a.Id == fixture.Accounts["Visa Rewards"]).IsChecked = true;
        await SettleAsync(() => reports.Loading);
        reports.AccountsLabel.ShouldBe("Visa Rewards");
        reports.SingleAccountId.ShouldBe(fixture.Accounts["Visa Rewards"]);
        spending.Query!.AccountIds.ShouldBe([fixture.Accounts["Visa Rewards"]]);
        reports.SelectAllAccountsCommand.Execute(null);
        await SettleAsync(() => reports.Loading);
        reports.AccountsLabel.ShouldBe("All accounts");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Exports_each_report_table_as_csv()
    {
        await FixtureAsync();
        var (window, shell) = await ShowAsync();
        var reports = await OpenReportsAsync(shell, ReportKind.IncomeExpense);
        var saved = new List<(string Name, string Content)>();
        reports.SaveFile = (name, content) =>
        {
            saved.Add((name, content));
            return Task.FromResult<string?>(name);
        };

        window.Named<Button>("ExportCsvButton").Command!.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => saved.Count == 1, "export");
        var lines = saved[0].Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        saved[0].Name.ShouldBe("income-vs-expense.csv");
        lines[0].ShouldBe("Month,Income,Expense,Net");
        lines.Length.ShouldBe(1 + 12 + 1);
        lines[^1].ShouldStartWith("Total,");
        var ie = (IncomeExpenseReportViewModel)reports.SelectedReport;
        lines[1].ShouldBe(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ie.Rows[0].Month:yyyy-MM},{ie.Rows[0].Income / 100m:F2},{ie.Rows[0].Expense / 100m:F2},{ie.Rows[0].Net / 100m:F2}"));
        _host.Get<Services.StatusService>().Message.ShouldContain("income-vs-expense.csv");

        foreach (var kind in new[] { ReportKind.Spending, ReportKind.NetWorth })
        {
            reports.SelectedReport = reports.Reports.Single(r => r.Kind == kind);
            await SettleAsync(() => reports.Loading);
            await reports.ExportCsvCommand.ExecuteAsync(null);
        }

        saved.Select(s => s.Name).ShouldBe(["income-vs-expense.csv", "spending.csv", "net-worth.csv"]);
        saved[1].Content.ShouldStartWith("Group,Category,Amount,Previous period\r\n");
        saved[1].Content.ShouldContain("Everyday,Groceries,");
        saved[2].Content.Split("\r\n")[0].ShouldBe("Date,Assets,Liabilities,Net worth,Everyday Checking,Bills Checking,Emergency Savings,Wallet,Visa Rewards,Travel Mastercard,Home Mortgage,Brokerage");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Csv_fields_are_quoted_when_needed()
    {
        await Task.CompletedTask;
        ReportFormat.Csv([["a", "b,c", "say \"hi\"", "line\nbreak"]]).ShouldBe("a,\"b,c\",\"say \"\"hi\"\"\",\"line\nbreak\"\r\n");
        ReportFormat.CsvAmount(-123_456, "USD").ShouldBe("-1234.56");
        ReportFormat.CsvAmount(500, "JPY").ShouldBe("500");
    }

    [AvaloniaFact]
    public async Task Creates_a_goal_with_the_wizard_and_shows_its_card()
    {
        await Task.Run(() => _host.Get<IAccountService>().CreateAccountAsync(new CreateAccountRequest("Checking", AccountType.Checking, "USD", Today.AddMonths(-1), 5_000_00), Ct));
        var brokerage = await Task.Run(() => _host.Get<IAccountService>().CreateAccountAsync(new CreateAccountRequest("Brokerage", AccountType.Investment, "USD", Today.AddMonths(-1), 2_000_00), Ct));
        var (window, shell) = await ShowAsync();
        shell.PrimaryItems.Single(i => i.PageType == typeof(GoalsViewModel)).NavigateCommand.Execute(null);
        var goals = _host.Get<GoalsViewModel>();
        await SettleAsync(() => goals.Loading);
        goals.ShowEmptyState.ShouldBeTrue();

        window.Named<Button>("EmptyNewGoalButton").Command!.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => goals.Wizard is not null && shell.Dialogs.Current is NewGoalViewModel, "wizard open");
        var wizard = goals.Wizard!;
        window.FindNamed<TextBox>("GoalNameBox").ShouldNotBeNull();

        // Step 1 validates, then moves on.
        wizard.ConfirmCommand.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => wizard.HasError, "name required");
        wizard.IsFirstStep.ShouldBeTrue();
        var target = BudgetMonth.Of(Today).AddMonths(9);
        wizard.Name = "Vacation";
        wizard.Amount = 3_000_00;
        wizard.TargetDate = target.ToDateTime(TimeOnly.MinValue);
        wizard.ConfirmCommand.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => wizard.IsSecondStep, "step 2");
        wizard.SummaryText.ShouldContain("$300.00");
        wizard.AccountOptions.Select(a => a.Name).ShouldBe(["No linked account", "Brokerage"]);
        wizard.SelectedAccount = wizard.AccountOptions[1];
        window.Named<Button>("GoalConfirmButton").Command!.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is null, "wizard closed");
        await SettleAsync(() => goals.Loading);

        var card = goals.Goals.ShouldHaveSingleItem();
        card.Name.ShouldBe("Vacation");
        card.Goal.GroupName.ShouldBe("Goals");
        card.Goal.LinkedAccountId.ShouldBe(brokerage.Id);
        card.LinkedAccountText.ShouldBe("Kept in Brokerage ($2,000.00)");
        card.MonthlyNeedText.ShouldBe("$300.00 a month needed to finish on time");
        card.Status.ShouldBe(GoalStatus.NoPace);
        window.GetVisualDescendants().OfType<GoalProgressRing>().ShouldHaveSingleItem();
        window.GetVisualDescendants().OfType<TextBlock>().ShouldContain(t => t.Text == "Vacation");

        // The what-if slider updates the projection live.
        card.ExtraPerMonth = 400;
        card.Status.ShouldBe(GoalStatus.OnTrack);
        card.ProjectionText.ShouldContain("(8 months)");
        card.ExtraPerMonth = 100;
        card.Status.ShouldBe(GoalStatus.Behind);

        // Assigning money updates the card through BudgetChanged; the pace is the 3-month average.
        await Task.Run(() => _host.Get<IBudgetService>().AssignAsync(card.CategoryId, Today, 600_00, Ct));
        await UiTestHelpers.WaitUntilAsync(() => goals.Goals.SingleOrDefault()?.Goal.Available == 600_00, "card refreshed");
        await SettleAsync(() => goals.Loading);
        var refreshed = goals.Goals.Single();
        refreshed.Goal.AveragePace.ShouldBe(200_00);
        refreshed.ExtraPerMonth.ShouldBe(100);          // the slider value survives a refresh
        refreshed.Progress.ShouldBe(0.2, 0.0001);
        refreshed.Projection.MonthsToGo.ShouldBe(8);    // 2,400 left at 300 a month
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dashboard_shows_ready_to_assign_from_the_budget_service_and_refreshes()
    {
        var (window, shell) = await ShowAsync();
        var home = _host.Get<HomeViewModel>();
        await SettleAsync(() => home.Loading);
        home.ShowEmptyState.ShouldBeTrue();
        window.Named<Button>("HomeAddAccountButton").IsVisible.ShouldBeTrue();

        await FixtureAsync(2_000);
        home.OnNavigatedTo(null);
        await SettleAsync(() => home.Loading);
        home.ShowDashboard.ShouldBeTrue();
        var budget = _host.Get<IBudgetService>();
        var month = await Task.Run(() => budget.GetMonthAsync(Today, Ct));
        home.ReadyToAssign.ShouldBe(month.ReadyToAssign);
        window.Named<TextBlock>("ReadyToAssignValue").Text.ShouldBe(home.ReadyToAssignText);
        var summary = await Task.Run(() => _host.Get<IRegisterQuery>().GetSummaryAsync(null, Ct));
        home.ReviewCount.ShouldBe(summary.UnapprovedCount);
        home.AccountGroups.Select(g => g.Title).ShouldBe(["Cash", "Credit", "Tracking"]);
        home.AccountGroups.Sum(g => g.Accounts.Count).ShouldBe(8);
        home.Alerts.Count.ShouldBeInRange(1, HomeViewModel.AlertCount);
        var fixtureStart = (await Task.Run(() => _host.Get<IAccountService>().GetAccountsAsync(false, Ct))).Min(a => a.OpeningDate);
        home.NetWorthValues.Count.ShouldBe(BudgetMonth.Between(fixtureStart, Today) + 1);
        window.Named<Border>("UpcomingBillsCard").ShouldNotBeNull();
        window.Named<Border>("ForecastCard").ShouldNotBeNull();

        // Assigning money publishes BudgetChanged; the card follows the budget service.
        var groceries = month.Groups.SelectMany(g => g.Categories).First(c => c.Name == "Groceries");
        await Task.Run(() => budget.AssignAsync(groceries.Id, Today, groceries.Assigned.Amount + 123_45, Ct));
        await UiTestHelpers.WaitUntilAsync(() => home.ReadyToAssign.Amount == month.ReadyToAssign.Amount - 123_45, "RTA refreshed");
        (await Task.Run(() => budget.GetMonthAsync(Today, Ct))).ReadyToAssign.ShouldBe(home.ReadyToAssign);

        // Cards link to their screens.
        window.Named<Button>("AssignButton").Command!.Execute(null);
        shell.CurrentPage.ShouldBeOfType<BudgetViewModel>();
        home.StartReviewCommand.Execute(null);
        shell.CurrentPage.ShouldBeOfType<ReviewViewModel>();
        home.OpenNetWorthCommand.Execute(null);
        shell.CurrentPage.ShouldBeOfType<ReportsViewModel>();
        ((ReportsViewModel)shell.CurrentPage).SelectedReport.Kind.ShouldBe(ReportKind.NetWorth);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dashboard_uses_one_column_when_narrow()
    {
        await FixtureAsync(1_000);
        var (window, shell) = await ShowAsync();
        var home = _host.Get<HomeViewModel>();
        home.OnNavigatedTo(null);
        await SettleAsync(() => home.Loading);
        var view = window.GetVisualDescendants().OfType<HomeView>().Single();
        view.IsTwoColumn.ShouldBeTrue();
        Grid.GetColumn(window.Named<Border>("ReviewCard")).ShouldBe(2);

        window.Width = 900;
        await SettleAsync(() => home.Loading);
        view.IsTwoColumn.ShouldBeFalse();
        Grid.GetColumn(window.Named<Border>("ReviewCard")).ShouldBe(0);
        Grid.GetRow(window.Named<Border>("NetWorthCard")).ShouldBe(6);
        window.Close();
    }
}
