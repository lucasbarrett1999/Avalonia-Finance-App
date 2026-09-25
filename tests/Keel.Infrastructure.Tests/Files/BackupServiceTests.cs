using System.IO.Compression;
using System.Text;
using Keel.Application.Backup;
using Keel.Application.Files;
using Keel.Domain;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Keel.Infrastructure.Tests.Files;

/// <summary>F-SET-1 backups and restore, PRD 10 verification, PRD 11 pre-migration backups.</summary>
public sealed class BackupServiceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    private static CancellationToken Ct => CancellationToken.None;

    private IBackupService Backups => _host.Get<IBackupService>();

    private IDataDirectory Data => _host.Get<IDataDirectory>();

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Backup_now_writes_a_timestamped_verified_zip_with_the_database_and_attachments()
    {
        await _host.CheckingAsync("Everyday");
        var attachments = Data.AttachmentsDirectoryFor(_host.FilePath);
        Directory.CreateDirectory(Path.Combine(attachments, "2026"));
        await File.WriteAllTextAsync(Path.Combine(attachments, "2026", "receipt.txt"), "receipt");

        var backup = await Backups.BackupNowAsync(Ct);

        backup.Kind.ShouldBe(BackupKind.Manual);
        Path.GetDirectoryName(backup.Path).ShouldBe(Data.BackupsDirectory);
        backup.FileName.ShouldMatch(@"^Ledger-\d{8}-\d{6}\.zip$");
        backup.SizeBytes.ShouldBeGreaterThan(0);
        using (var zip = ZipFile.OpenRead(backup.Path))
        {
            zip.Entries.Select(e => e.FullName).ShouldBe(["backup.json", "Ledger.keel", "Ledger.keel-attachments/2026/receipt.txt"], ignoreOrder: true);
        }

        (await Backups.VerifyAsync(backup.Path, Ct)).ShouldBeTrue();
        (await Backups.ListBackupsAsync(Ct)).ShouldHaveSingleItem().Path.ShouldBe(backup.Path);
        Directory.EnumerateDirectories(Data.BackupsDirectory).ShouldBeEmpty(); // work folders cleaned up
    }

    [Fact]
    public async Task Restore_brings_back_the_backed_up_state_and_keeps_a_before_restore_backup()
    {
        await _host.CheckingAsync("Kept");
        var attachments = Data.AttachmentsDirectoryFor(_host.FilePath);
        Directory.CreateDirectory(attachments);
        await File.WriteAllTextAsync(Path.Combine(attachments, "a.txt"), "old");
        var backup = await Backups.BackupNowAsync(Ct);

        await _host.CheckingAsync("Added later");
        await File.WriteAllTextAsync(Path.Combine(attachments, "b.txt"), "new");

        await Backups.RestoreAsync(backup.Path, Ct);

        await using (var db = _host.Db())
        {
            (await db.Accounts.Select(a => a.Name).ToListAsync()).ShouldBe(["Kept"]);
        }

        Directory.EnumerateFiles(attachments).Select(Path.GetFileName).ShouldBe(["a.txt"]);
        var list = await Backups.ListBackupsAsync(Ct);
        list.Select(b => b.Kind).ShouldBe([BackupKind.Manual, BackupKind.BeforeRestore], ignoreOrder: true);

        // The file still works for writes after the restore.
        await _host.CheckingAsync("After restore");
    }

    [Fact]
    public async Task Verify_rejects_damaged_foreign_and_newer_backups_and_restore_refuses_them()
    {
        await _host.CheckingAsync();
        var garbage = Path.Combine(Data.BackupsDirectory, "Ledger-20260101-000000.zip");
        Directory.CreateDirectory(Data.BackupsDirectory);
        await File.WriteAllBytesAsync(garbage, Encoding.UTF8.GetBytes("not a zip"));
        (await Backups.VerifyAsync(garbage, Ct)).ShouldBeFalse();
        await Should.ThrowAsync<BackupVerificationException>(() => Backups.RestoreAsync(garbage, Ct));

        // A backup whose database knows a migration this build does not (made by a newer Keel).
        var good = await Backups.BackupNowAsync(Ct);
        var newer = Path.Combine(Data.BackupsDirectory, "Ledger-20260102-000000.zip");
        File.Copy(good.Path, newer);
        using (var zip = ZipFile.Open(newer, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("Ledger.keel")!;
            var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".keel");
            entry.ExtractToFile(temp);
            using (var connection = new SqliteConnection(BackupArchive.UnpooledConnectionString(temp)))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """INSERT INTO "__EFMigrationsHistory" VALUES ('29990101000000_FromTheFuture', '99.0');""";
                command.ExecuteNonQuery();
            }

            entry.Delete();
            zip.CreateEntryFromFile(temp, "Ledger.keel");
            File.Delete(temp);
        }

        (await Backups.VerifyAsync(newer, Ct)).ShouldBeFalse();
        var ex = await Should.ThrowAsync<BackupVerificationException>(() => Backups.RestoreAsync(newer, Ct));
        ex.Message.ShouldContain("newer version");

        // Nothing was replaced.
        await using var db = _host.Db();
        (await db.Accounts.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Daily_backup_runs_once_per_day_and_keeps_the_newest_n_automatic_backups()
    {
        await _host.CheckingAsync();
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        var service = new BackupService(_host.Get<KeelDbContextFactory>(), Data, clock, NullLogger<BackupService>.Instance);
        var manual = await service.BackupNowAsync(Ct);

        for (var day = 1; day <= 5; day++)
        {
            clock.Now = new DateTimeOffset(2026, 9, day, 9, 0, 0, TimeSpan.Zero);
            var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
            (await service.RunDailyBackupAsync(today, keep: 3, Ct)).ShouldNotBeNull().Kind.ShouldBe(BackupKind.Automatic);
            clock.Now = clock.Now.AddHours(5);
            (await service.RunDailyBackupAsync(today, keep: 3, Ct)).ShouldBeNull();
        }

        var list = await service.ListBackupsAsync(Ct);
        list.Where(b => b.Kind == BackupKind.Automatic).Select(b => b.CreatedAt.Day).ShouldBe([5, 4, 3]);
        list.ShouldContain(b => b.Path == manual.Path); // manual backups are never pruned
    }

    [Fact]
    public async Task Opening_a_file_that_needs_migrations_backs_it_up_first()
    {
        var path = Path.Combine(Data.BudgetsDirectory, "Old.keel");
        Directory.CreateDirectory(Data.BudgetsDirectory);
        await using (var db = KeelDbContextFactory.CreateForFile(path))
        {
            var migrations = db.Database.GetMigrations().ToList();
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(migrations[^2]);
        }

        var files = new BudgetFileService(new KeelDbContextFactory(), NullLogger<BudgetFileService>.Instance, Data);
        var info = await files.OpenOrCreateAsync(path, Ct);

        info.AppliedMigrations.ShouldHaveSingleItem();
        var backup = Directory.EnumerateFiles(Data.BackupsDirectory, "Old-*-before-migration.zip").ShouldHaveSingleItem();
        BackupArchive.Parse(path, Path.GetFileName(backup))!.Value.Kind.ShouldBe(BackupKind.BeforeMigration);

        // Opening it again migrates nothing and backs up nothing.
        await files.OpenOrCreateAsync(path, Ct);
        Directory.EnumerateFiles(Data.BackupsDirectory, "Old-*").Count().ShouldBe(1);
        SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData("Default-20260924-101500.zip", BackupKind.Manual)]
    [InlineData("Default-20260924-101500-auto.zip", BackupKind.Automatic)]
    [InlineData("Default-20260924-101500-auto-2.zip", BackupKind.Automatic)]
    [InlineData("Default-20260924-101500-before-migration.zip", BackupKind.BeforeMigration)]
    [InlineData("Default-20260924-101500-before-restore.zip", BackupKind.BeforeRestore)]
    public void Backup_names_round_trip(string name, BackupKind kind)
    {
        var parsed = BackupArchive.Parse("/x/Default.keel", name).ShouldNotBeNull();
        parsed.Kind.ShouldBe(kind);
        parsed.LocalTime.ShouldBe(new DateTime(2026, 9, 24, 10, 15, 0));
        BackupArchive.FileNameFor("/x/Default.keel", kind, parsed.LocalTime).ShouldBe(name.Replace("-2.zip", ".zip", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Other-20260924-101500.zip")]
    [InlineData("Default-2026-09-24.zip")]
    [InlineData("Default-20260924-101500-weird.zip")]
    public void Other_files_are_not_backups_of_the_file(string name) =>
        BackupArchive.Parse("/x/Default.keel", name).ShouldBeNull();

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
