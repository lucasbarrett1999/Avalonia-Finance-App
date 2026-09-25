using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Categories;
using Keel.Application.Import;
using Keel.Application.Settings;
using Keel.Application.Stats;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Settings;
using Keel.Desktop.Views;
using Keel.Domain;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Desktop.Tests;

/// <summary>Settings → Privacy &amp; Stats (PRD 4, ADR 0102) and the instrumentation behind it.</summary>
public sealed class StatsPageTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    private async Task<(ShellWindow Window, ShellViewModel Shell)> ShowAsync()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private async Task<StatsSettingsViewModel> StatsAsync(ShellViewModel shell)
    {
        shell.NavigateToSettings("Privacy");
        var stats = _host.Current<StatsSettingsViewModel>();
        await stats.Loading;
        Dispatcher.UIThread.RunJobs();
        return stats;
    }

    [AvaloniaFact]
    public async Task The_stats_page_shows_every_prd_metric_with_its_target_and_explanation()
    {
        var (window, shell) = await ShowAsync();
        var stats = await StatsAsync(shell);

        stats.Metrics.Select(m => m.Id).ShouldBe(["first-budget", "review", "reimport", "cold-start", "scroll"]);
        stats.Metrics.ShouldAllBe(m => m.Outcome == StatsOutcome.NotMeasured);
        stats.Metrics.ShouldAllBe(m => m.TargetText.StartsWith("Target: ", StringComparison.Ordinal) && m.Explanation.Length > 40 && m.ValueText.Length > 0);
        stats.Metrics.Single(m => m.Id == "scroll").ValueText.ShouldBe(Keel.Desktop.Resources.Strings.Stats_Scroll_None);
        stats.ComputedText.ShouldNotBeNull();

        var view = window.GetVisualDescendants().OfType<Keel.Desktop.Views.Settings.StatsSettingsView>().Single();
        view.Named<ItemsControl>("StatsList").ItemCount.ShouldBe(5);
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        texts.ShouldContain(Keel.Desktop.Resources.Strings.Stats_Intro);
        texts.ShouldContain(Keel.Desktop.Resources.Strings.Stats_NotMeasured);
        texts.ShouldContain("Target: " + Keel.Desktop.Resources.Strings.Stats_Review_Target);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Metrics_follow_the_data_assignments_reviews_reimports()
    {
        var accounts = _host.Get<IAccountService>();
        var account = await Task.Run(() => accounts.CreateAccountAsync(new CreateAccountRequest("Everyday", AccountType.Checking, "USD", new DateOnly(2026, 1, 1), 500_00), Ct));
        var category = await Task.Run(() => _host.Get<ICategoryService>().CreateCategoryAsync("Everyday", "Groceries", Ct));
        await Task.Run(() => _host.Get<IBudgetService>().AssignAsync(category.Id, new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1), 100_00, Ct));
        var batch = new ImportBatch(account.Id, [new IncomingTransaction(new DateOnly(2026, 8, 1), -12_00, "Corner Grocer", CategoryId: category.Id), new IncomingTransaction(new DateOnly(2026, 8, 2), -3_00, "Kiosk", CategoryId: category.Id)]);
        await Task.Run(() => _host.Get<IImportService>().ImportTransactionsAsync(TransactionSource.File, batch, Ct));
        await Task.Run(() => _host.Get<IImportService>().ImportTransactionsAsync(TransactionSource.File, batch, Ct));
        var imported = await Task.Run(async () =>
        {
            await using var db = await _host.Get<IDbContextFactory<KeelDbContext>>().CreateDbContextAsync();
            return await db.Transactions.Where(t => t.Source == TransactionSource.File).Select(t => t.Id).ToListAsync();
        });
        await Task.Run(() => _host.Get<Keel.Application.Ledger.ITransactionService>().ApproveAsync(imported, Ct));

        var (window, shell) = await ShowAsync();
        var stats = await StatsAsync(shell);

        var first = stats.Metrics.Single(m => m.Id == "first-budget");
        first.Outcome.ShouldBe(StatsOutcome.Met);
        first.Detail!.ShouldContain("first change recorded in this file");
        var review = stats.Metrics.Single(m => m.Id == "review");
        review.Outcome.ShouldBe(StatsOutcome.Met);
        review.ValueText.ShouldContain("2 of 2");
        var reimport = stats.Metrics.Single(m => m.Id == "reimport");
        reimport.Outcome.ShouldBe(StatsOutcome.Met);
        reimport.ValueText.ShouldContain("2 of 2 rows in 1 re-imports");
        review.AutomationName.ShouldContain(review.OutcomeText);

        // Refresh picks up measurements from settings.json.
        _host.Get<IAppSettingsStore>().Update(s => s with { Stats = s.Stats with { ColdStart = new ColdStartSample(3_400, 100_000, false, DateTime.UtcNow) } });
        await stats.RefreshAsync();
        var cold = stats.Metrics.Single(m => m.Id == "cold-start");
        cold.Outcome.ShouldBe(StatsOutcome.NotMet);
        cold.ValueText.ShouldContain("100,000 transactions");
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_first_window_frame_records_the_cold_start_once()
    {
        _host.Sessions.ColdStartedAt = DateTime.UtcNow.AddMilliseconds(-1_200);
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        var instrumentation = shell.StatsInstrumentation!;
        await UiTestHelpers.WaitUntilAsync(
            () =>
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                return !instrumentation.IsColdStartPending;
            },
            "interactive frame");
        await instrumentation.ColdStartRecording;

        var sample = _host.Get<IAppSettingsStore>().Current.Stats.ColdStart.ShouldNotBeNull();
        sample.Milliseconds.ShouldBeGreaterThanOrEqualTo(1_200);
        sample.Milliseconds.ShouldBeLessThan(60_000);
        sample.Transactions.ShouldBe(0);
        sample.Encrypted.ShouldBeFalse();
        _host.Sessions.ColdStartedAt.ShouldBeNull("only the first session of a process measures");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Scrolling_a_100k_register_records_frame_times()
    {
        await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(_host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(), Ct));
        var (window, shell) = await ShowAsync();
        shell.AccountItems[0].NavigateCommand.Execute(null);
        var vm = _host.Get<AccountsViewModel>();
        await vm.SettleAsync();
        vm.RowCount.ShouldBe(100_000);
        var view = window.Register();
        var meter = view.FrameMeter.ShouldNotBeNull();

        // Scroll through the register while the render timer ticks, then let the burst end.
        for (var i = 0; i < 60; i++)
        {
            view.Grid.ScrollIntoView(vm.Rows[i * 1_500], null);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(5);
        }

        await UiTestHelpers.WaitUntilAsync(
            () =>
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                return !meter.IsRunning;
            },
            "scroll burst ended");

        meter.Frames.Count.ShouldBeGreaterThanOrEqualTo(ScrollFrameMeter.MinimumFrames);
        var sample = _host.Get<IAppSettingsStore>().Current.Stats.RegisterScroll.ShouldNotBeNull();
        sample.Rows.ShouldBe(100_000);
        sample.Frames.ShouldBe(meter.Frames.Count, "saved when the last burst ended");
        sample.AverageMilliseconds.ShouldBeGreaterThan(0);
        sample.P95Milliseconds.ShouldBeGreaterThanOrEqualTo(sample.AverageMilliseconds * 0.5);
        window.Close();
    }

    [Fact]
    public void The_frame_meter_times_frames_only_while_rows_are_requested()
    {
        var clock = 0L;
        var requests = new Queue<Action<TimeSpan>>();
        var meter = new ScrollFrameMeter(requests.Enqueue, () => clock);
        var ended = 0;
        meter.BurstEnded += (_, _) => ended++;

        meter.Activity();
        meter.Activity();
        requests.Count.ShouldBe(1, "one frame loop per burst");
        var time = TimeSpan.Zero;
        for (var i = 0; i < 40; i++)
        {
            clock += System.Diagnostics.Stopwatch.Frequency / 100; // 10 ms of activity per frame
            meter.Activity();
            time += TimeSpan.FromMilliseconds(i % 10 == 0 ? 40 : 16);
            requests.Dequeue()(time);
        }

        meter.Frames.Count.ShouldBe(39);
        clock += System.Diagnostics.Stopwatch.Frequency; // quiet for a second
        requests.Dequeue()(time + TimeSpan.FromMilliseconds(16));
        requests.ShouldBeEmpty();
        ended.ShouldBe(1);
        meter.IsRunning.ShouldBeFalse();

        var sample = meter.Summarize(100_000, DateTime.UtcNow).ShouldNotBeNull();
        sample.Frames.ShouldBe(40);
        sample.P95Milliseconds.ShouldBe(40);
        sample.AverageMilliseconds.ShouldBe(17.8);
        ScrollFrameMeter.Summarize([16, 16], 100_000, DateTime.UtcNow).ShouldBeNull("too few frames");
    }

    [Fact]
    public void Metric_rows_compare_values_with_the_prd_targets()
    {
        var now = DateTime.UtcNow;
        var report = new StatsReport(
            new FirstBudgetStat(now.AddMinutes(-40), FirstBudgetStart.FirstLaunch, now.AddMinutes(-20)),
            new ReviewAccuracyStat(100, 85, now.AddDays(-30)),
            new ReimportStat(2, 50, 49, now),
            new ColdStartSample(1_500, 100_000, true, now),
            new RegisterScrollSample(100_000, 300, 8.4, 12.9, now),
            now);

        var rows = StatsSettingsViewModel.Build(report, now);

        rows[0].Outcome.ShouldBe(StatsOutcome.NotMet);
        rows[0].ValueText.ShouldBe("20 min 0 s");
        rows[1].Outcome.ShouldBe(StatsOutcome.NotMet, "85% is not above 85%");
        rows[2].Outcome.ShouldBe(StatsOutcome.NotMet, "the target is every row");
        rows[3].Outcome.ShouldBe(StatsOutcome.Met);
        rows[3].ValueText.ShouldContain("encrypted");
        rows[4].Outcome.ShouldBe(StatsOutcome.Met);
        StatsSettingsViewModel.Duration(TimeSpan.FromSeconds(42)).ShouldBe("42 s");
        StatsSettingsViewModel.Duration(TimeSpan.FromMinutes(75)).ShouldBe("1 h 15 min");
        StatsSettingsViewModel.Duration(TimeSpan.FromHours(50)).ShouldBe("2 days 2 h");
    }

    [AvaloniaFact]
    public void A_fresh_install_records_its_first_launch()
    {
        using var host = TestHost.CreateFirstRun();
        var launch = host.Get<IAppSettingsStore>().Current.Stats.FirstLaunchAt.ShouldNotBeNull();
        launch.ShouldBeInRange(DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddSeconds(1));
        _host.Get<IAppSettingsStore>().Current.Stats.FirstLaunchAt.ShouldBeNull("returning users started before the measurement existed");
    }
}
