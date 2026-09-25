using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Debt;
using Keel.Application.Undo;
using Keel.Desktop.Controls;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Goals;
using Keel.Desktop.ViewModels.Reports;
using Keel.Desktop.Views;
using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Domain.Debt;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using LiveChartsCore.SkiaSharpView.Avalonia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.Tests;

/// <summary>M9b headless flows: PNG export of report charts, the budget health report and the debt payoff planner.</summary>
public sealed class DebtHealthExportTests : IDisposable
{
    private readonly FakeFileDialogs _files = new();
    private readonly TestHost _host;
    private readonly string _out = Path.Combine(Path.GetTempPath(), "keel-png-tests", Guid.NewGuid().ToString("N"));

    public DebtHealthExportTests() => _host = TestHost.Create(services => services.AddSingleton<IFileDialogs>(_files));

    public void Dispose()
    {
        _host.Dispose();
        try
        {
            Directory.Delete(_out, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

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

        // LiveCharts redraws through its own throttled loop.
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(40);
        }
    }

    [AvaloniaFact]
    public async Task Every_report_exports_its_chart_as_a_2x_png()
    {
        await FixtureAsync();
        var (window, shell) = await ShowAsync();
        shell.PrimaryItems.Single(i => i.PageType == typeof(ReportsViewModel)).NavigateCommand.Execute(null);
        var reports = _host.Get<ReportsViewModel>();
        await SettleAsync(() => reports.Loading);
        Directory.CreateDirectory(_out);
        var shots = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");

        foreach (var report in reports.Reports)
        {
            reports.SelectedReport = report;
            await SettleAsync(() => reports.Loading);
            report.HasData.ShouldBeTrue(report.Kind.ToString());
            var path = Path.Combine(_out, report.PngFileName);
            _files.SavePngs.Enqueue(path);
            var chart = ChartImage.FindChart(window.Named<ContentControl>("ReportHost"));
            chart.ShouldNotBeNull(report.Kind.ToString());

            window.Named<Button>("ExportPngButton").Command!.Execute(null);
            await UiTestHelpers.WaitUntilAsync(() => File.Exists(path), "png written for " + report.Kind);
            await UiTestHelpers.WaitUntilAsync(() => _host.Get<StatusService>().Message.Contains(report.PngFileName, StringComparison.Ordinal), "status");

            var bytes = await File.ReadAllBytesAsync(path);
            bytes.Take(8).ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "PNG signature");
            var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            width.ShouldBe((int)Math.Ceiling(chart.Bounds.Width * ChartImage.Scale), report.Kind.ToString());
            height.ShouldBe((int)Math.Ceiling(chart.Bounds.Height * ChartImage.Scale), report.Kind.ToString());
            bytes.Length.ShouldBeGreaterThan(2_000, "the image has content");
            if (!string.IsNullOrEmpty(shots))
            {
                Directory.CreateDirectory(shots);
                File.Copy(path, Path.Combine(shots, "Export-" + report.PngFileName), overwrite: true);
            }
        }

        _files.PngSuggestions.ShouldBe(reports.Reports.Select(r => r.PngFileName));
        _files.PngSuggestions.ShouldContain("budget-health.png");

        // Cancelling the picker writes nothing; the keyboard shortcut runs the same command.
        var before = Directory.GetFiles(_out).Length;
        window.Press(Avalonia.Input.PhysicalKey.E, _host.Get<PlatformShortcuts>().Command() | Avalonia.Input.RawInputModifiers.Shift);
        await UiTestHelpers.WaitUntilAsync(() => _files.PngSuggestions.Count == reports.Reports.Count + 1, "shortcut asked for a file");
        Directory.GetFiles(_out).Length.ShouldBe(before);
        window.Close();
    }

    private async Task<(Guid Visa, Guid Car, Guid Store)> DebtsAsync()
    {
        var accounts = _host.Get<IAccountService>();
        var opening = Today.AddMonths(-2);
        return await Task.Run(async () =>
        {
            await accounts.CreateAccountAsync(new CreateAccountRequest("Checking", AccountType.Checking, "USD", opening, 6_000_00), Ct);
            var visa = await accounts.CreateAccountAsync(new CreateAccountRequest("Visa", AccountType.CreditCard, "USD", opening, -2_000_00, Debt: new DebtTerms(2400, 60_00)), Ct);
            var car = await accounts.CreateAccountAsync(new CreateAccountRequest("Car loan", AccountType.Loan, "USD", opening, -9_000_00, Debt: new DebtTerms(649, 250_00)), Ct);
            var store = await accounts.CreateAccountAsync(new CreateAccountRequest("Store card", AccountType.CreditCard, "USD", opening, -400_00), Ct);
            return (visa.Id, car.Id, store.Id);
        });
    }

    [AvaloniaFact]
    public async Task Debt_payoff_tab_plans_debts_sets_undoable_targets_and_asks_for_missing_details()
    {
        var (visa, car, store) = await DebtsAsync();
        var (window, shell) = await ShowAsync();
        shell.PrimaryItems.Single(i => i.PageType == typeof(GoalsViewModel)).NavigateCommand.Execute(null);
        var goals = _host.Get<GoalsViewModel>();
        await SettleAsync(() => goals.Loading);

        // Key 2 opens the Debt payoff tab.
        window.Press(Avalonia.Input.PhysicalKey.Digit2);
        goals.SelectedTab.ShouldBe(GoalsTab.DebtPayoff);
        var debt = goals.DebtPayoff!;
        await SettleAsync(() => debt.Loading);
        debt.ShowContent.ShouldBeTrue();
        debt.Rows.Select(r => r.Name).ShouldBe(["Visa", "Car loan"]); // avalanche
        debt.Missing.ShouldHaveSingleItem().AccountId.ShouldBe(store);
        debt.Missing[0].MissingText.ShouldBe(Keel.Desktop.Resources.Strings.Debt_Missing_Both);
        debt.HeadlineText.ShouldStartWith("Debt-free by ");
        debt.ComparisonText.ShouldContain("sooner"); // no extra yet, but Visa's minimum rolls over to the loan once it is paid off
        window.GetVisualDescendants().OfType<Views.Goals.DebtPayoffView>().ShouldHaveSingleItem();
        window.GetVisualDescendants().OfType<CartesianChart>().Single(c => c.Name == "DebtChart").Series.Count().ShouldBe(3);

        // Extra per month and ordering re-plan at once, matching the calculator.
        debt.ExtraPerMonth = 200_00;
        await SettleAsync(() => debt.Loading);
        var expected = DebtPayoffCalculator.Plan(
            [new DebtInput(visa, "Visa", 2_000_00, 2400, 60_00), new DebtInput(car, "Car loan", 9_000_00, 649, 250_00)], 200_00, DebtOrdering.Avalanche);
        debt.Overview!.Plan.PayoffMonths.ShouldBe(expected.PayoffMonths);
        debt.Rows[0].PaymentText.ShouldBe("$260.00");
        debt.ComparisonText.ShouldContain("sooner");
        debt.SelectedOrdering = debt.Orderings.Single(o => o.Value == DebtOrdering.Snowball);
        await SettleAsync(() => debt.Loading);
        debt.Overview!.Plan.Ordering.ShouldBe(DebtOrdering.Snowball);

        // One click sets both targets (the loan gets a new payment category), undone in one step.
        window.Named<Button>("SetTargetsButton").Command!.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => debt.Rows.Count == 2 && debt.Rows.All(r => r.CategoryText.Contains("target", StringComparison.Ordinal)), "targets shown");
        _host.Get<StatusService>().Message.ShouldBe("Set 2 debt-payment targets and created 1 payment categories");
        var budget = _host.Get<IBudgetService>();
        var visaCategory = debt.Overview!.Debts.Single(d => d.AccountId == visa).PaymentCategoryId!.Value;
        (await Task.Run(() => budget.GetTargetAsync(visaCategory, Ct)))!.Amount.ShouldBe(260_00);
        _host.Get<IUndoService>().NextUndo.ShouldBe(LedgerAction.SetTarget);
        shell.UndoCommand.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => debt.Rows.Count == 2 && debt.Overview!.Debts.All(d => d.CurrentTarget is null), "targets undone");
        (await Task.Run(() => budget.GetTargetAsync(visaCategory, Ct))).ShouldBeNull();

        // "Edit account" on a debt without details adds it to the plan.
        var editing = debt.EditAccountCommand.ExecuteAsync(debt.Missing[0]);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is AccountEditorViewModel, "editor");
        var editor = (AccountEditorViewModel)shell.Dialogs.Current!;
        editor.ShowDebtTerms.ShouldBeTrue();
        editor.InterestRateText = "abc";
        await editor.ConfirmCommand.ExecuteAsync(null);
        editor.Error.ShouldBe(Keel.Desktop.Resources.Strings.Debt_Editor_RateInvalid);
        editor.InterestRateText = "26.99";
        editor.MinimumPayment = 30_00;
        await editor.ConfirmCommand.ExecuteAsync(null);
        await editing;
        await UiTestHelpers.WaitUntilAsync(() => debt.Rows.Count == 3 && debt.Missing.Count == 0, "store card planned");
        (await Task.Run(() => _host.Get<IAccountService>().GetAccountAsync(store, Ct)))!.InterestRateBps.ShouldBe(2699);

        // A row opens its register; key 1 returns to the goal cards.
        debt.Rows.First(r => r.AccountId == car).Open.Execute(debt.Rows.First(r => r.AccountId == car));
        shell.CurrentPage.ShouldBeOfType<AccountsViewModel>();
        await _host.Get<AccountsViewModel>().SettleAsync();
        _host.Get<AccountsViewModel>().Account!.Id.ShouldBe(car);
        shell.PrimaryItems.Single(i => i.PageType == typeof(GoalsViewModel)).NavigateCommand.Execute(null);
        await SettleAsync(() => goals.Loading);
        window.Press(Avalonia.Input.PhysicalKey.Digit1);
        goals.SelectedTab.ShouldBe(GoalsTab.Goals);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Debt_payoff_tab_shows_its_empty_state_without_debts_and_the_palette_opens_it()
    {
        await Task.Run(() => _host.Get<IAccountService>().CreateAccountAsync(new CreateAccountRequest("Checking", AccountType.Checking, "USD", Today, 1_000_00), Ct));
        var (window, shell) = await ShowAsync();
        _host.Get<AppCommands>().Build(shell).Single(c => c.Id == "goals-debt").Execute();
        var goals = _host.Get<GoalsViewModel>();
        shell.CurrentPage.ShouldBe(goals);
        goals.SelectedTab.ShouldBe(GoalsTab.DebtPayoff);
        var debt = goals.DebtPayoff!;
        await SettleAsync(() => debt.Loading);
        debt.ShowEmptyState.ShouldBeTrue();
        debt.ShowContent.ShouldBeFalse();
        window.Named<Button>("DebtAddAccountButton").IsEffectivelyVisible.ShouldBeTrue();
        debt.SetTargetsCommand.CanExecute(null).ShouldBeFalse();

        // Adding a loan from the empty state plans it once it has details.
        var adding = debt.AddAccountCommand.ExecuteAsync(null);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is AccountEditorViewModel, "editor");
        var editor = (AccountEditorViewModel)shell.Dialogs.Current!;
        editor.SelectedType.Value.ShouldBe(AccountType.Loan);
        editor.Name = "Student loan";
        editor.OpeningBalance = 12_000_00;
        editor.InterestRateText = "4.5";
        editor.MinimumPayment = 130_00;
        await editor.ConfirmCommand.ExecuteAsync(null);
        await adding;
        await UiTestHelpers.WaitUntilAsync(() => debt.Rows.Count == 1, "loan planned");
        debt.Rows[0].RateText.ShouldBe("4.5%");
        debt.Rows[0].OwedText.ShouldBe("$12,000.00");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Account_editor_shows_debt_terms_for_liabilities_and_parses_rates()
    {
        await Task.CompletedTask;
        var editor = new AccountEditorViewModel(_host.Get<IAccountService>());
        editor.ShowDebtTerms.ShouldBeFalse(); // Checking
        foreach (var type in new[] { AccountType.CreditCard, AccountType.LineOfCredit, AccountType.Loan, AccountType.OtherLiability })
        {
            editor.SelectedType = editor.Types.Single(t => t.Value == type);
            editor.ShowDebtTerms.ShouldBeTrue(type.ToString());
        }

        editor.SelectedType = editor.Types.Single(t => t.Value == AccountType.Investment);
        editor.ShowDebtTerms.ShouldBeFalse();

        AccountEditorViewModel.TryParseRate("19.99", out var bps).ShouldBeTrue();
        bps.ShouldBe(1999);
        AccountEditorViewModel.TryParseRate(" 7 % ", out bps).ShouldBeTrue();
        bps.ShouldBe(700);
        AccountEditorViewModel.TryParseRate("", out bps).ShouldBeTrue();
        bps.ShouldBeNull();
        AccountEditorViewModel.TryParseRate("0", out bps).ShouldBeTrue();
        bps.ShouldBe(0);
        AccountEditorViewModel.TryParseRate("100.01", out _).ShouldBeFalse();
        AccountEditorViewModel.TryParseRate("-1", out _).ShouldBeFalse();
        AccountEditorViewModel.TryParseRate("1.234", out _).ShouldBeFalse();
        AccountEditorViewModel.FormatRate(1999).ShouldBe("19.99");
        AccountEditorViewModel.FormatRate(700).ShouldBe("7");
    }

    [AvaloniaFact]
    public async Task Budget_health_report_explains_each_metric_and_drills_down_and_home_shows_age_of_money()
    {
        var fixture = await FixtureAsync();
        var month = BudgetMonth.Of(Today);
        await Task.Run(() => _host.Get<IBudgetService>().SetTargetAsync(new TargetDto(fixture.Categories["Groceries"], TargetType.MonthlySpending, 600_00), Ct));
        var (window, shell) = await ShowAsync();
        shell.PrimaryItems.Single(i => i.PageType == typeof(HomeViewModel)).NavigateCommand.Execute(null);
        var home = _host.Get<HomeViewModel>();
        await SettleAsync(() => home.Loading);
        home.AgeOfMoneyText.ShouldEndWith("days");
        home.AgeOfMoneyValues.Count.ShouldBeGreaterThan(1);
        window.Named<Button>("BudgetHealthButton").Command!.Execute(null);

        var reports = _host.Get<ReportsViewModel>();
        shell.CurrentPage.ShouldBe(reports);
        await SettleAsync(() => reports.Loading);
        var health = reports.SelectedReport.ShouldBeOfType<BudgetHealthReportViewModel>();
        health.HasData.ShouldBeTrue();
        window.Named<DropDownButton>("AccountsFilterButton").IsVisible.ShouldBeFalse();
        health.AgeOfMoneyText.ShouldBe(home.AgeOfMoneyText);
        health.MonthHeading.ShouldContain(month.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
        health.MonthsAheadDetail.ShouldNotBeNullOrWhiteSpace();
        health.TargetsDetail.ShouldContain("of 1 targets funded");
        health.Rows.Count.ShouldBeInRange(2, 12);
        health.Rows[0].Days.ShouldNotBeNull(); // the history starts with the first month that has an age
        var report = health.Report!;
        health.OverspentText.ShouldBe(report.OverspentCount.ToString(System.Globalization.CultureInfo.CurrentCulture));

        // CSV has every metric and the history.
        var csv = health.ToCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        csv[0].ShouldBe("Metric,Value");
        csv.ShouldContain(l => l.StartsWith("Age of money (days),", StringComparison.Ordinal));
        csv.Length.ShouldBe(1 + 9 + health.Rows.Count);

        // A history point opens that month's transactions; an overspent row its category.
        health.Rows[^1].Open!.Execute(health.Rows[^1]);
        var register = _host.Get<AccountsViewModel>();
        shell.CurrentPage.ShouldBe(register);
        await register.SettleAsync();
        register.SearchText.ShouldBe($"date:{month:yyyy-MM-dd}..{health.Rows[^1].Date:yyyy-MM-dd}");
        if (health.Overspent.Count > 0)
        {
            shell.PrimaryItems.Single(i => i.PageType == typeof(ReportsViewModel)).NavigateCommand.Execute(null);
            await SettleAsync(() => reports.Loading);
            health.Overspent[0].Open!.Execute(health.Overspent[0]);
            await register.SettleAsync();
            register.SelectedCategoryFilter.Id.ShouldBe(health.Overspent[0].CategoryId);
        }

        window.Close();
    }
}
