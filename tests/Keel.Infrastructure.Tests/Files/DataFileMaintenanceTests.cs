using System.IO.Compression;
using System.Text;
using Keel.Application.Files;
using Keel.Application.Setup;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Files;

/// <summary>Integrity check, change location, diagnostic bundle (PRD 10) and the setup checklist.</summary>
public sealed class DataFileMaintenanceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    private static CancellationToken Ct => CancellationToken.None;

    private IDataFileMaintenance Maintenance => _host.Get<IDataFileMaintenance>();

    private IDataDirectory Data => _host.Get<IDataDirectory>();

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Integrity_check_passes_on_a_healthy_file_and_runs_once_per_day()
    {
        var today = new DateOnly(2026, 9, 24);
        var first = await Maintenance.CheckIntegrityAsync(Ct);
        first.IsOk.ShouldBeTrue();
        first.Problems.ShouldBeEmpty();

        // CheckIntegrityAsync recorded the real local day; a different day is due, the same day is not.
        var recordedToday = DateOnly.FromDateTime(DateTime.Now);
        (await Maintenance.CheckIntegrityIfDueAsync(recordedToday, Ct)).ShouldBeNull();
        (await Maintenance.CheckIntegrityIfDueAsync(today.AddYears(1), Ct)).ShouldNotBeNull().IsOk.ShouldBeTrue();
    }

    [Fact]
    public async Task Integrity_check_reports_a_damaged_file()
    {
        await _host.CheckingAsync();
        for (var i = 0; i < 50; i++)
        {
            await _host.CheckingAsync("Account " + i);
        }

        // Damage every b-tree page header behind SQLite's back (after moving the WAL into the file).
        await using (var db = _host.Db())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
        }

        SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(_host.FilePath);
        const int pageSize = 4096;
        for (var offset = pageSize; offset + 8 < bytes.Length; offset += pageSize)
        {
            for (var j = 1; j < 8; j++)
            {
                bytes[offset + j] ^= 0x5A;
            }
        }

        await File.WriteAllBytesAsync(_host.FilePath, bytes);

        var result = await Maintenance.CheckIntegrityAsync(Ct);
        result.IsOk.ShouldBeFalse();
        result.Problems.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Copy_to_writes_a_verified_copy_with_attachments_and_refuses_to_overwrite()
    {
        await _host.CheckingAsync("Moved account");
        var attachments = Data.AttachmentsDirectoryFor(_host.FilePath);
        Directory.CreateDirectory(attachments);
        await File.WriteAllTextAsync(Path.Combine(attachments, "r.txt"), "r");
        var destination = Path.Combine(Data.Root, "synced", "Ledger.keel");

        await Maintenance.CopyToAsync(destination, Ct);

        File.Exists(destination).ShouldBeTrue();
        File.Exists(Path.Combine(Data.AttachmentsDirectoryFor(destination), "r.txt")).ShouldBeTrue();
        await using (var db = Keel.Infrastructure.Persistence.KeelDbContextFactory.CreateForFile(destination))
        {
            (await db.Accounts.Select(a => a.Name).SingleAsync()).ShouldBe("Moved account");
        }

        await Should.ThrowAsync<IOException>(() => Maintenance.CopyToAsync(destination, Ct));
        await Should.ThrowAsync<IOException>(() => Maintenance.CopyToAsync(_host.FilePath, Ct));

        SqliteConnection.ClearAllPools();
        await Maintenance.DeleteBudgetFileAsync(destination, Ct);
        File.Exists(destination).ShouldBeFalse();
        Directory.Exists(Data.AttachmentsDirectoryFor(destination)).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => Maintenance.DeleteBudgetFileAsync(_host.FilePath, Ct));
    }

    [Fact]
    public async Task Diagnostic_bundle_has_logs_and_a_schema_only_summary_without_rows_payees_or_amounts()
    {
        var checking = await _host.CheckingAsync("Zanzibar Checking", opening: 987_654_32);
        await _host.AddAsync(checking.Id, -123_456_78, payee: "Quixotic Payee Name", memo: "Secret memo text");
        Directory.CreateDirectory(Data.LogsDirectory);
        await File.WriteAllTextAsync(Path.Combine(Data.LogsDirectory, "keel-20260924.log"), "2026-09-24 INF Keel starting");

        var zipPath = await Maintenance.CreateDiagnosticBundleAsync(Path.Combine(Data.Root, "diagnostics"), "1.0.0-test", Ct);

        Path.GetFileName(zipPath).ShouldMatch(@"^keel-diagnostics-\d{8}-\d{6}\.zip$");
        using var zip = ZipFile.OpenRead(zipPath);
        zip.Entries.Select(e => e.FullName).ShouldBe(["schema-summary.json", "logs/keel-20260924.log"], ignoreOrder: true);
        string summary;
        using (var reader = new StreamReader(zip.GetEntry("schema-summary.json")!.Open(), Encoding.UTF8))
        {
            summary = await reader.ReadToEndAsync();
        }

        summary.ShouldContain("\"Transactions\"");
        summary.ShouldContain("IX_Transactions_ReviewQueue");
        summary.ShouldContain("1.0.0-test");
        summary.ShouldContain("ReviewQueueIndex");
        foreach (var secret in new[] { "Zanzibar", "Quixotic", "Secret memo", "98765432", "987,654", "12345678", "123,456" })
        {
            summary.ShouldNotContain(secret);
        }
    }

    [Fact]
    public async Task Setup_progress_follows_the_data_and_remembers_dismissal()
    {
        var setup = _host.Get<ISetupProgressService>();
        var empty = await setup.GetAsync(Ct);
        empty.ShouldBe(new SetupProgress(false, false, false, false));
        empty.CompletedSteps.ShouldBe(1);

        var groceries = await _host.CategoryAsync("Groceries");
        await _host.CheckingAsync();
        (await setup.GetAsync(Ct)).CompletedSteps.ShouldBe(3);

        await _host.Get<Keel.Application.Budget.IBudgetService>().AssignAsync(groceries, new DateOnly(2026, 8, 1), 100_00, Ct);
        var done = await setup.GetAsync(Ct);
        done.IsComplete.ShouldBeTrue();
        done.IsDismissed.ShouldBeFalse();

        await setup.DismissAsync(Ct);
        (await setup.GetAsync(Ct)).IsDismissed.ShouldBeTrue();
    }
}
