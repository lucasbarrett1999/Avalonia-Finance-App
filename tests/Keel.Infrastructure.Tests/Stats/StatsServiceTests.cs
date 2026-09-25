using Keel.Application.Budget;
using Keel.Application.Import;
using Keel.Application.Ledger;
using Keel.Application.Settings;
using Keel.Application.Stats;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Stats;
using Keel.Infrastructure.Tests.Import.Pipeline;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Stats;

/// <summary>PRD 4 metrics computed from the audit log, the Setting table and settings.json (ADR 0102).</summary>
public sealed class StatsServiceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    private static CancellationToken Ct => CancellationToken.None;

    private IStatsService Stats => _host.Get<IStatsService>();

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task An_empty_file_has_nothing_measured_yet()
    {
        var report = await Stats.GetAsync(Ct);

        report.FirstBudget.FirstAssignedAt.ShouldBeNull();
        report.FirstBudget.Duration.ShouldBeNull();
        report.Review.Approved.ShouldBe(0);
        report.Review.Rate.ShouldBeNull();
        report.Reimport.Reimports.ShouldBe(0);
        report.Reimport.Rate.ShouldBeNull();
        report.ColdStart.ShouldBeNull();
        report.Scroll.ShouldBeNull();
    }

    [Fact]
    public async Task First_budget_runs_from_the_first_launch_to_the_first_assignment()
    {
        var launch = DateTime.UtcNow.AddMinutes(-9);
        _host.Get<IAppSettingsStore>().Update(s => s with { Stats = s.Stats with { FirstLaunchAt = launch } });
        await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        var month = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        await _host.Get<IBudgetService>().AssignAsync(groceries, month, 250_00, Ct);
        await _host.Get<IBudgetService>().AssignAsync(groceries, month, 300_00, Ct);

        var stat = (await Stats.GetAsync(Ct)).FirstBudget;

        stat.Start.ShouldBe(FirstBudgetStart.FirstLaunch);
        stat.StartedAt.ShouldBe(launch);
        stat.Duration!.Value.TotalMinutes.ShouldBeInRange(8.9, 10);
        stat.Duration.Value.ShouldBeLessThan(StatsReport.FirstBudgetTarget);
    }

    [Fact]
    public async Task Without_a_recorded_first_launch_the_clock_starts_at_the_files_first_change()
    {
        await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        await _host.Get<IBudgetService>().AssignAsync(groceries, new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1), 10_00, Ct);

        var stat = (await Stats.GetAsync(Ct)).FirstBudget;

        stat.Start.ShouldBe(FirstBudgetStart.FileCreated);
        stat.FirstAssignedAt.ShouldNotBeNull();
        stat.Duration!.Value.ShouldBeLessThan(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Review_accuracy_counts_imported_rows_approved_with_the_category_they_were_imported_with()
    {
        var account = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        var dining = await _host.CategoryAsync("Dining");
        await _host.ImportAsync(
            account.Id,
            new IncomingTransaction(new DateOnly(2026, 8, 3), -12_00, "Corner Grocer", CategoryId: groceries),
            new IncomingTransaction(new DateOnly(2026, 8, 4), -13_00, "Corner Grocer", CategoryId: groceries),
            new IncomingTransaction(new DateOnly(2026, 8, 5), -14_00, "Corner Grocer", CategoryId: groceries),
            new IncomingTransaction(new DateOnly(2026, 8, 6), -15_00, "Noodle Bar"));
        var rows = await _host.ImportedAsync(account.Id);
        rows.ShouldAllBe(t => !t.IsApproved);
        var transactions = _host.Get<ITransactionService>();

        // Two approved as imported, one re-categorized before approval, one categorized from nothing.
        await transactions.ApproveAsync([rows[0].Id, rows[1].Id], Ct);
        await transactions.CategorizeAsync([rows[2].Id], dining, Ct);
        await transactions.ApproveAsync([rows[2].Id], Ct);
        await transactions.CategorizeAsync([rows[3].Id], dining, Ct);
        await transactions.ApproveAsync([rows[3].Id], Ct);

        // A manual entry (approved when entered) does not count.
        await _host.AddAsync(account.Id, -5_00, category: groceries);

        var review = (await Stats.GetAsync(Ct)).Review;
        review.Approved.ShouldBe(4);
        review.Unchanged.ShouldBe(2);
        review.Rate.ShouldBe(0.5);
        review.Since.ShouldBeLessThan(DateTime.UtcNow.AddDays(-29));
    }

    [Fact]
    public async Task Reimporting_a_file_records_how_many_rows_were_flagged_duplicate()
    {
        var account = await _host.CheckingAsync();
        var csv = ImportKit.Utf8("""
            Date,Description,Amount
            2026-08-01,Corner Grocer,-12.00
            2026-08-02,Noodle Bar,-8.50
            2026-08-03,Payroll,2000.00
            """);

        await _host.ImportFileAsync(account, "august.csv", csv);
        (await Stats.GetAsync(Ct)).Reimport.Reimports.ShouldBe(0);

        var again = await _host.ImportFileAsync(account, "august.csv", csv);
        again.DuplicatesSkipped.ShouldBe(3);
        var stat = (await Stats.GetAsync(Ct)).Reimport;
        stat.Reimports.ShouldBe(1);
        stat.Rows.ShouldBe(3);
        stat.Flagged.ShouldBe(3);
        stat.Rate.ShouldBe(1.0);
        stat.LastAt.ShouldNotBeNull();

        // A different file is a first import, and sync or manual rows never count.
        await _host.ImportFileAsync(account, "september.csv", ImportKit.Utf8("Date,Description,Amount\n2026-09-01,Corner Grocer,-12.00\n"));
        await _host.Get<IImportService>().ImportTransactionsAsync(TransactionSource.Manual, new ImportBatch(account.Id, [new IncomingTransaction(new DateOnly(2026, 9, 2), -1_00, "Kiosk")]), Ct);
        (await Stats.GetAsync(Ct)).Reimport.Reimports.ShouldBe(1);

        // Nothing readable is stored: only hashes and counts.
        await using var db = _host.Db();
        var json = await db.Settings.Where(s => s.Key == ImportStatsLog.Key).Select(s => s.ValueJson).SingleAsync();
        foreach (var readable in new[] { "Grocer", "Noodle", "Payroll", "august", "12.00", "8.50", "2000" })
        {
            json.ShouldNotContain(readable);
        }

        var stored = System.Text.Json.JsonSerializer.Deserialize<ImportStatsLog>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)).ShouldNotBeNull();
        stored.Batches.ShouldAllBe(hash => System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-f]{64}$"), "batches are SHA-256 fingerprints only");
    }

    [Fact]
    public void The_import_log_keeps_bounded_lists()
    {
        var log = new ImportStatsLog();
        for (var i = 0; i < ImportStatsLog.MaxBatches + 20; i++)
        {
            log = log.Record("hash-" + i, 1, 0, DateTime.UtcNow);
        }

        log.Batches.Count.ShouldBe(ImportStatsLog.MaxBatches);
        log.Batches[^1].ShouldBe("hash-" + (ImportStatsLog.MaxBatches + 19));
        for (var i = 0; i < ImportStatsLog.MaxReimports + 5; i++)
        {
            log = log.Record(log.Batches[^1], 2, 2, DateTime.UtcNow);
        }

        log.Reimports.Count.ShouldBe(ImportStatsLog.MaxReimports);
    }

    [Fact]
    public async Task Cold_start_and_scroll_samples_come_from_settings_json()
    {
        var cold = new ColdStartSample(1_234, 100_000, Encrypted: false, DateTime.UtcNow);
        var scroll = new RegisterScrollSample(100_000, 240, 9.5, 14.2, DateTime.UtcNow);
        _host.Get<IAppSettingsStore>().Update(s => s with { Stats = s.Stats with { ColdStart = cold, RegisterScroll = scroll } });

        var report = await Stats.GetAsync(Ct);

        report.ColdStart.ShouldBe(cold);
        report.Scroll.ShouldBe(scroll);
    }
}
