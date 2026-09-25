using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Keel.Application.Backup;
using Keel.Application.Files;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Infrastructure.Tests.Encryption;

/// <summary>F-SET-4 optional SQLCipher encryption on real files (ADR 0101).</summary>
public sealed class EncryptionTests : IAsyncLifetime
{
    private const string Passphrase = "correct horse battery staple";
    private readonly EncryptionTestHost _host = new();

    private static CancellationToken Ct => CancellationToken.None;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Encrypting_backs_up_converts_and_the_file_then_opens_only_with_its_passphrase()
    {
        var session = await _host.OpenAsync();
        await EncryptionTestHost.AccountAsync(session, "Everyday");
        await EncryptionTestHost.AccountAsync(session, "Savings");

        await _host.ConvertAsync(new BudgetFileEncryptionChange(null, Passphrase));

        EncryptionTestHost.IsPlain(_host.FilePath).ShouldBeFalse();
        File.Exists(_host.FilePath + BudgetFileEncryptionService.ConvertingSuffix).ShouldBeFalse();
        var backup = Directory.EnumerateFiles(_host.Data.BackupsDirectory, "Home-*-before-encryption.zip").ShouldHaveSingleItem();
        BackupArchive.Parse(_host.FilePath, Path.GetFileName(backup))!.Value.Kind.ShouldBe(BackupKind.BeforeEncryption);
        EncryptionTestHost.IsPlain(ExtractDatabase(backup)).ShouldBeTrue("the pre-encryption backup is the plain file");

        // Same app run: the key ring reopens it (restore and move reopen the file without asking again).
        var reopened = await _host.OpenAsync();
        (await EncryptionTestHost.AccountNamesAsync(reopened)).ShouldBe(["Everyday", "Savings"]);
        (await reopened.GetRequiredService<IBudgetFileEncryption>().GetStatusAsync(Ct)).IsEncrypted.ShouldBeTrue();

        // A new app run without a remembered key: locked, wrong passphrase refused, right one opens.
        await _host.CloseAllAsync();
        _host.Restart();
        var locked = await Should.ThrowAsync<BudgetFileLockedException>(() => _host.OpenAsync());
        locked.WrongPassphrase.ShouldBeFalse();
        locked.Path.ShouldBe(_host.FilePath);
        var wrong = await Should.ThrowAsync<BudgetFileLockedException>(() => _host.OpenAsync(new BudgetFileUnlock("Correct horse battery staple")));
        wrong.WrongPassphrase.ShouldBeTrue();
        var unlocked = await _host.OpenAsync(new BudgetFileUnlock(Passphrase));
        var info = await unlocked.GetRequiredService<IBudgetFileService>().OpenOrCreateAsync(_host.FilePath, Ct);
        info.IsEncrypted.ShouldBeTrue();
        (await EncryptionTestHost.AccountNamesAsync(unlocked)).ShouldBe(["Everyday", "Savings"]);

        // Writes keep working, in WAL mode like plain files.
        await EncryptionTestHost.AccountAsync(unlocked, "Travel");
        await using (var db = await unlocked.GetRequiredService<IDbContextFactory<KeelDbContext>>().CreateDbContextAsync())
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            (await command.ExecuteScalarAsync())!.ToString().ShouldBe("wal");
        }

        // It is a standard SQLCipher 4 file: the passphrase itself opens it in other SQLCipher tools.
        await _host.CloseAllAsync();
        await using (var standard = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _host.FilePath, Password = Passphrase, Pooling = false }.ToString()))
        {
            await standard.OpenAsync();
            await using var command = standard.CreateCommand();
            command.CommandText = """SELECT COUNT(*) FROM "Accounts";""";
            Convert.ToInt32(await command.ExecuteScalarAsync()).ShouldBe(3);
        }

        // Without a key the bytes are not SQLite at all.
        var raw = await File.ReadAllBytesAsync(_host.FilePath);
        Encoding.UTF8.GetString(raw).ShouldNotContain("Everyday");
    }

    [Fact]
    public async Task A_remembered_key_opens_the_file_in_a_new_app_run_and_forgetting_it_locks_it_again()
    {
        var session = await _host.OpenAsync();
        await EncryptionTestHost.AccountAsync(session, "Everyday");
        await _host.ConvertAsync(new BudgetFileEncryptionChange(null, Passphrase, RememberKey: true));

        var fileId = SqlCipher.FileId(_host.FilePath).ShouldNotBeNull();
        _host.Secrets.Contains(SecretKeys.BudgetFile(fileId)).ShouldBeTrue();
        var stored = (await _host.Secrets.GetAsync(SecretKeys.BudgetFile(fileId)))!;
        stored.ShouldNotContain(Passphrase, Case.Insensitive, "the derived key is stored, never the passphrase");
        stored.ShouldStartWith("x'");

        _host.Restart();
        var opened = await _host.OpenAsync();
        var encryption = opened.GetRequiredService<IBudgetFileEncryption>();
        var status = await encryption.GetStatusAsync(Ct);
        status.IsEncrypted.ShouldBeTrue();
        status.IsKeyRemembered.ShouldBeTrue();
        status.SecretStore!.Backend.ShouldBe(SecretStoreBackend.InMemory);
        (await encryption.VerifyPassphraseAsync(Passphrase, Ct)).ShouldBeTrue();
        (await encryption.VerifyPassphraseAsync("nope", Ct)).ShouldBeFalse();

        await encryption.SetKeyRememberedAsync(false, Ct);
        _host.Secrets.Count.ShouldBe(0);
        (await encryption.GetStatusAsync(Ct)).IsKeyRemembered.ShouldBeFalse();
        await _host.CloseAllAsync();
        _host.Restart();
        await Should.ThrowAsync<BudgetFileLockedException>(() => _host.OpenAsync());

        // Remembering from the unlock prompt stores it again.
        await _host.OpenAsync(new BudgetFileUnlock(Passphrase, Remember: true));
        _host.Secrets.Contains(SecretKeys.BudgetFile(fileId)).ShouldBeTrue();
    }

    [Fact]
    public async Task Removing_encryption_needs_the_passphrase_backs_up_the_encrypted_file_and_forgets_the_key()
    {
        var session = await _host.OpenAsync();
        await EncryptionTestHost.AccountAsync(session, "Everyday");
        await _host.ConvertAsync(new BudgetFileEncryptionChange(null, Passphrase, RememberKey: true));
        var before = await File.ReadAllBytesAsync(_host.FilePath);

        await Should.ThrowAsync<BudgetFileLockedException>(() => _host.ConvertAsync(new BudgetFileEncryptionChange("wrong passphrase", null)));
        (await File.ReadAllBytesAsync(_host.FilePath)).ShouldBe(before, "a refused conversion leaves the file alone");

        await _host.ConvertAsync(new BudgetFileEncryptionChange(Passphrase, null));

        EncryptionTestHost.IsPlain(_host.FilePath).ShouldBeTrue();
        _host.Secrets.Count.ShouldBe(0);
        var backup = Directory.EnumerateFiles(_host.Data.BackupsDirectory, "Home-*-before-decryption.zip").ShouldHaveSingleItem();
        var copy = ExtractDatabase(backup);
        EncryptionTestHost.IsPlain(copy).ShouldBeFalse("the pre-decryption backup stays encrypted");
        SqlCipher.CanOpen(copy, SqlCipher.KeyForFile(copy, Passphrase)).ShouldBeTrue();

        _host.Restart();
        var plain = await _host.OpenAsync();
        (await EncryptionTestHost.AccountNamesAsync(plain)).ShouldBe(["Everyday"]);
        (await plain.GetRequiredService<IBudgetFileEncryption>().GetStatusAsync(Ct)).IsEncrypted.ShouldBeFalse();

        // Converting the wrong way round is refused.
        await Should.ThrowAsync<InvalidOperationException>(() => _host.ConvertAsync(new BudgetFileEncryptionChange(Passphrase, null)));
    }

    [Fact]
    public async Task Backups_of_an_encrypted_file_stay_encrypted_with_the_same_key_and_restore_keeps_the_encryption()
    {
        var session = await _host.OpenAsync();
        await EncryptionTestHost.AccountAsync(session, "Kept");
        await _host.ConvertAsync(new BudgetFileEncryptionChange(null, Passphrase));
        session = await _host.OpenAsync();
        var backups = session.GetRequiredService<IBackupService>();
        var salt = SqlCipher.FileId(_host.FilePath);

        var backup = await backups.BackupNowAsync(Ct);
        (await backups.VerifyAsync(backup.Path, Ct)).ShouldBeTrue();
        var copy = ExtractDatabase(backup.Path);
        SqlCipher.FileId(copy).ShouldBe(salt, "the copy keeps the salt, so the passphrase opens it");
        SqlCipher.CanOpen(copy, null).ShouldBeFalse();
        SqlCipher.CanOpen(copy, SqlCipher.KeyForFile(copy, Passphrase)).ShouldBeTrue();
        ReadManifest(backup.Path).GetProperty("Encrypted").GetBoolean().ShouldBeTrue();

        await EncryptionTestHost.AccountAsync(session, "Added later");
        await backups.RestoreAsync(backup.Path, Ct);
        await _host.CloseAllAsync();
        SqlCipher.FileId(_host.FilePath).ShouldBe(salt);
        session = await _host.OpenAsync();
        (await EncryptionTestHost.AccountNamesAsync(session)).ShouldBe(["Kept"]);

        // The plain backup taken before encryption restores into the encrypted file, which stays encrypted.
        var plainBackup = Directory.EnumerateFiles(_host.Data.BackupsDirectory, "Home-*-before-encryption.zip").Single();
        await EncryptionTestHost.AccountAsync(session, "After");
        await session.GetRequiredService<IBackupService>().RestoreAsync(plainBackup, Ct);
        await _host.CloseAllAsync();
        EncryptionTestHost.IsPlain(_host.FilePath).ShouldBeFalse();
        SqlCipher.FileId(_host.FilePath).ShouldBe(salt);
        session = await _host.OpenAsync();
        (await EncryptionTestHost.AccountNamesAsync(session)).ShouldBe(["Kept"]);
        await EncryptionTestHost.AccountAsync(session, "Writable after restore");
    }

    [Fact]
    public async Task A_backup_under_another_passphrase_asks_for_it_and_then_restores_into_the_open_file()
    {
        // An encrypted backup made under passphrase A...
        var session = await _host.OpenAsync();
        await EncryptionTestHost.AccountAsync(session, "From A");
        await _host.ConvertAsync(new BudgetFileEncryptionChange(null, "passphrase A"));
        session = await _host.OpenAsync();
        var fromA = await session.GetRequiredService<IBackupService>().BackupNowAsync(Ct);

        // ...restored after the file was decrypted and re-encrypted under passphrase B.
        await _host.ConvertAsync(new BudgetFileEncryptionChange("passphrase A", null));
        await _host.ConvertAsync(new BudgetFileEncryptionChange(null, "passphrase B"));
        _host.Restart();
        session = await _host.OpenAsync(new BudgetFileUnlock("passphrase B"));
        await EncryptionTestHost.AccountAsync(session, "Only in B");
        var backups = session.GetRequiredService<IBackupService>();

        var locked = await Should.ThrowAsync<BudgetFileLockedException>(() => backups.RestoreAsync(fromA.Path, Ct));
        locked.WrongPassphrase.ShouldBeFalse();
        (await Should.ThrowAsync<BudgetFileLockedException>(() => backups.RestoreAsync(fromA.Path, "passphrase C", Ct))).WrongPassphrase.ShouldBeTrue();
        await backups.RestoreAsync(fromA.Path, "passphrase A", Ct);

        await _host.CloseAllAsync();
        _host.Restart();
        session = await _host.OpenAsync(new BudgetFileUnlock("passphrase B"));
        (await EncryptionTestHost.AccountNamesAsync(session)).ShouldBe(["From A"]);
    }

    [Fact]
    public async Task An_encrypted_file_that_needs_migrations_is_backed_up_encrypted_and_migrated()
    {
        var key = SqlCipher.NewKey(Passphrase);
        await using (var db = KeelDbContextFactory.CreateForFile(_host.FilePath, key))
        {
            var migrations = db.Database.GetMigrations().ToList();
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(migrations[^2]);
        }

        SqliteConnection.ClearAllPools();
        EncryptionTestHost.IsPlain(_host.FilePath).ShouldBeFalse();
        await Should.ThrowAsync<BudgetFileLockedException>(() => _host.OpenAsync());

        var session = _host.NewSession();
        var info = await session.GetRequiredService<IBudgetFileService>().OpenOrCreateAsync(_host.FilePath, new BudgetFileUnlock(Passphrase), Ct);

        info.AppliedMigrations.ShouldHaveSingleItem();
        info.IsEncrypted.ShouldBeTrue();
        var backup = Directory.EnumerateFiles(_host.Data.BackupsDirectory, "Home-*-before-migration.zip").ShouldHaveSingleItem();
        var copy = ExtractDatabase(backup);
        EncryptionTestHost.IsPlain(copy).ShouldBeFalse();
        BackupArchive.VerifyDatabase(copy, key);
        await EncryptionTestHost.AccountAsync(session, "Migrated");
    }

    [Fact]
    public async Task Integrity_check_move_and_diagnostics_work_on_an_encrypted_file_without_revealing_the_key()
    {
        var session = await _host.OpenAsync();
        await EncryptionTestHost.AccountAsync(session, "Zanzibar Checking");
        await _host.ConvertAsync(new BudgetFileEncryptionChange(null, Passphrase));
        session = await _host.OpenAsync();
        var maintenance = session.GetRequiredService<IDataFileMaintenance>();

        (await maintenance.CheckIntegrityAsync(Ct)).IsOk.ShouldBeTrue();

        var destination = Path.Combine(_host.Data.Root, "elsewhere", "Home.keel");
        await maintenance.CopyToAsync(destination, Ct);
        EncryptionTestHost.IsPlain(destination).ShouldBeFalse();
        SqlCipher.FileId(destination).ShouldBe(SqlCipher.FileId(_host.FilePath));
        SqlCipher.CanOpen(destination, SqlCipher.KeyForFile(destination, Passphrase)).ShouldBeTrue();

        var zipPath = await maintenance.CreateDiagnosticBundleAsync(Path.Combine(_host.Data.Root, "diagnostics"), "1.0.0-test", Ct);
        string summary;
        using (var zip = ZipFile.OpenRead(zipPath))
        using (var reader = new StreamReader(zip.GetEntry("schema-summary.json")!.Open(), Encoding.UTF8))
        {
            summary = await reader.ReadToEndAsync();
        }

        summary.ShouldContain("\"encrypted\": \"yes\"");
        summary.ShouldContain("\"cipher_version\": \"4.");
        summary.ShouldContain("\"Accounts\"");
        summary.ShouldNotContain("Zanzibar");
        var key = SqlCipher.KeyForFile(_host.FilePath, Passphrase);
        foreach (var secret in new[] { Passphrase, key, key[2..34] })
        {
            summary.ShouldNotContain(secret, Case.Insensitive);
            string.Join('\n', _host.Logs).ShouldNotContain(secret, Case.Insensitive);
        }

        _host.Logs.ShouldContain(l => l.Contains("encrypted: True", StringComparison.Ordinal));
    }

    [Fact]
    public void Keys_are_derived_like_sqlcipher_and_carry_the_file_salt()
    {
        var salt = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var key = SqlCipher.DeriveKey("pass", salt);
        key.Length.ShouldBe(2 + 96 + 1);
        key.ShouldEndWith("00112233445566778899AABBCCDDEEFF'");
        SqlCipher.FileIdOfKey(key).ShouldBe("00112233445566778899aabbccddeeff");
        SqlCipher.DeriveKey("pass", salt).ShouldBe(key);
        SqlCipher.DeriveKey("Pass", salt).ShouldNotBe(key);
        SqlCipher.NewKey("pass").ShouldNotBe(SqlCipher.NewKey("pass"));
        new BudgetFileUnlock("hunter2").ToString().ShouldNotContain("hunter2");
        new BudgetFileEncryptionChange(null, "hunter2").ToString().ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task Plain_files_are_not_encrypted_and_have_no_file_id()
    {
        var session = await _host.OpenAsync();
        SqlCipher.IsEncrypted(_host.FilePath).ShouldBeFalse();
        SqlCipher.FileId(_host.FilePath).ShouldBeNull();
        SqlCipher.IsEncrypted(Path.Combine(_host.Data.Root, "missing.keel")).ShouldBeFalse();
        var text = Path.Combine(_host.Data.Root, "notes.keel");
        await File.WriteAllTextAsync(text, "this is not a SQLite database");
        SqlCipher.IsEncrypted(text).ShouldBeFalse("a file that is not a whole number of pages is not a database at all");
        var status = await session.GetRequiredService<IBudgetFileEncryption>().GetStatusAsync(Ct);
        status.IsEncrypted.ShouldBeFalse();
        status.IsKeyRemembered.ShouldBeFalse();
        (await session.GetRequiredService<IBudgetFileEncryption>().VerifyPassphraseAsync(Passphrase, Ct)).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => session.GetRequiredService<IBudgetFileEncryption>().ConvertAsync(_host.FilePath, new BudgetFileEncryptionChange(null, Passphrase), Ct));
    }

    private string ExtractDatabase(string zipPath)
    {
        var target = Path.Combine(_host.Data.Root, "extract-" + Guid.NewGuid().ToString("N"), "Home.keel");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var zip = ZipFile.OpenRead(zipPath);
        zip.GetEntry("Home.keel")!.ExtractToFile(target);
        return target;
    }

    private static JsonElement ReadManifest(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var stream = zip.GetEntry(BackupArchive.ManifestEntry)!.Open();
        return JsonDocument.Parse(stream).RootElement.Clone();
    }
}
