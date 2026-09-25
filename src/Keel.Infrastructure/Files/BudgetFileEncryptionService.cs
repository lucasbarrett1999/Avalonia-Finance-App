using System.Security.Cryptography;
using System.Text;
using Keel.Application.Backup;
using Keel.Application.Files;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Files;

/// <summary>
/// Optional SQLCipher encryption of budget files (F-SET-4, ADR 0101): encrypt and decrypt by
/// <c>sqlcipher_export</c> into a verified copy that is swapped in after a verified backup, the state of the
/// open file, and the opt-in key cache in the OS secret store. Nothing here logs a key or passphrase.
/// </summary>
public sealed partial class BudgetFileEncryptionService(
    KeelDbContextFactory factory,
    IDataDirectory dataDirectory,
    TimeProvider time,
    ILogger<BudgetFileEncryptionService> logger,
    ISecretStore? secrets = null,
    ISecretStoreInfo? secretStoreInfo = null,
    BudgetFileKeyRing? keyRing = null) : IBudgetFileEncryption
{
    private readonly BudgetFileKeyRing _keyRing = keyRing ?? new BudgetFileKeyRing();

    /// <summary>Suffix of the converted copy while it is written and verified.</summary>
    public const string ConvertingSuffix = ".converting";

    /// <inheritdoc />
    public async Task<BudgetFileEncryptionStatus> GetStatusAsync(CancellationToken ct)
    {
        var description = secretStoreInfo is null ? null : await DescribeAsync(secretStoreInfo, ct).ConfigureAwait(false);
        if (factory.CurrentKey is not { } key)
        {
            return new BudgetFileEncryptionStatus(false, false, description);
        }

        var remembered = false;
        if (secrets is not null)
        {
            try
            {
                var stored = await secrets.GetAsync(SecretKeys.BudgetFile(SqlCipher.FileIdOfKey(key))).ConfigureAwait(false);
                remembered = stored is not null && SameKey(stored, key);
            }
            catch (SecretStoreException ex)
            {
                LogSecretStoreFailed(logger, ex);
            }
        }

        return new BudgetFileEncryptionStatus(true, remembered, description);
    }

    /// <inheritdoc />
    public Task<bool> VerifyPassphraseAsync(string passphrase, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        var key = factory.CurrentKey;
        var path = factory.CurrentPath;
        if (key is null || path is null || passphrase.Length == 0)
        {
            return Task.FromResult(false);
        }

        return Task.Run(() => SameKey(SqlCipher.DeriveKey(passphrase, System.Convert.FromHexString(SqlCipher.FileIdOfKey(key))), key), ct);
    }

    /// <inheritdoc />
    public async Task SetKeyRememberedAsync(bool remember, CancellationToken ct)
    {
        var key = factory.CurrentKey ?? throw new InvalidOperationException("The budget file is not encrypted.");
        if (secrets is null)
        {
            throw new SecretStoreException("No secret store is available.");
        }

        var name = SecretKeys.BudgetFile(SqlCipher.FileIdOfKey(key));
        if (remember)
        {
            await secrets.SetAsync(name, key).ConfigureAwait(false);
        }
        else
        {
            await secrets.DeleteAsync(name).ConfigureAwait(false);
        }

        LogRememberChanged(logger, remember);
    }

    /// <inheritdoc />
    public async Task ConvertAsync(string path, BudgetFileEncryptionChange change, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(change);
        if (!change.Encrypts && !change.Decrypts)
        {
            throw new ArgumentException("Only encrypting a plain file or decrypting an encrypted one is supported.", nameof(change));
        }

        var full = Path.GetFullPath(path);
        if (factory.CurrentPath is { } open && string.Equals(open, full, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Close the budget file before changing its encryption.");
        }

        var (oldId, newKey) = await Task.Run(() => ConvertFile(full, change), ct).ConfigureAwait(false);
        if (oldId is not null)
        {
            _keyRing.Forget(oldId);
            await ForgetRememberedAsync(oldId).ConfigureAwait(false);
        }

        if (newKey is not null)
        {
            var newId = SqlCipher.FileIdOfKey(newKey);
            _keyRing.Remember(newId, newKey);
            if (change.RememberKey && secrets is not null)
            {
                try
                {
                    await secrets.SetAsync(SecretKeys.BudgetFile(newId), newKey).ConfigureAwait(false);
                }
                catch (SecretStoreException ex)
                {
                    LogSecretStoreFailed(logger, ex);
                }
            }
        }

        LogConverted(logger, change.Encrypts);
    }

    private (string? OldId, string? NewKey) ConvertFile(string path, BudgetFileEncryptionChange change)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The budget file does not exist.", Path.GetFileName(path));
        }

        // No pooled connection may keep the old file (or its WAL) open while it is replaced.
        SqliteConnection.ClearAllPools();
        string? currentKey = null;
        string? oldId = null;
        if (change.CurrentPassphrase is { } passphrase)
        {
            oldId = SqlCipher.FileId(path) ?? throw new InvalidOperationException("The budget file is not encrypted.");
            currentKey = SqlCipher.KeyForFile(path, passphrase);
            if (!SqlCipher.CanOpen(path, currentKey))
            {
                throw new BudgetFileLockedException(path, wrongPassphrase: true);
            }
        }
        else if (SqlCipher.IsEncrypted(path))
        {
            throw new InvalidOperationException("The budget file is already encrypted.");
        }

        var newKey = change.NewPassphrase is { } fresh ? SqlCipher.NewKey(fresh) : null;
        var kind = change.Encrypts ? BackupKind.BeforeEncryption : BackupKind.BeforeDecryption;
        BackUp(path, currentKey, kind);

        var temp = path + ConvertingSuffix;
        DeleteIfExists(temp);
        try
        {
            SqlCipher.Export(path, currentKey, temp, newKey);
            BackupArchive.VerifyDatabase(temp, newKey);
            if (RowCount(path, currentKey) != RowCount(temp, newKey))
            {
                throw new BackupVerificationException("The converted copy does not have the same rows as the budget file.");
            }

            // The old WAL and shared-memory files belong to the old file; left next to the new one SQLite
            // would try to apply them to it. The export read through the WAL, so nothing is lost.
            DeleteIfExists(path + "-wal");
            DeleteIfExists(path + "-shm");
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            DeleteIfExists(temp);
            throw;
        }

        return (oldId, newKey);
    }

    private void BackUp(string path, string? key, BackupKind kind)
    {
        var zip = BackupPaths.UniqueZipPath(dataDirectory, path, kind, time.GetLocalNow().DateTime);
        BackupArchive.Write(path, dataDirectory.AttachmentsDirectoryFor(path), zip, kind, time.GetUtcNow().UtcDateTime, key);
        var work = Path.Combine(dataDirectory.BackupsDirectory, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            BackupArchive.ExtractAndVerify(zip, work, key);
        }
        catch
        {
            File.Delete(zip);
            throw;
        }
        finally
        {
            BackupArchive.TryDeleteDirectory(work);
        }
    }

    private static long RowCount(string path, string? key)
    {
        using var connection = new SqliteConnection(SqlCipher.ConnectionString(path, key, SqliteOpenMode.ReadOnly));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """SELECT (SELECT COUNT(*) FROM "Transactions") + (SELECT COUNT(*) FROM "AuditEvents") + (SELECT COUNT(*) FROM "Settings");""";
        return System.Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ForgetRememberedAsync(string fileId)
    {
        if (secrets is null)
        {
            return;
        }

        try
        {
            await secrets.DeleteAsync(SecretKeys.BudgetFile(fileId)).ConfigureAwait(false);
        }
        catch (SecretStoreException ex)
        {
            LogSecretStoreFailed(logger, ex);
        }
    }

    private async Task<SecretStoreDescription?> DescribeAsync(ISecretStoreInfo info, CancellationToken ct)
    {
        try
        {
            return await info.DescribeAsync(ct).ConfigureAwait(false);
        }
        catch (SecretStoreException ex)
        {
            LogSecretStoreFailed(logger, ex);
            return null;
        }
    }

    private static bool SameKey(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a.ToUpperInvariant()), Encoding.ASCII.GetBytes(b.ToUpperInvariant()));

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Budget file converted (encrypted: {Encrypted})")]
    private static partial void LogConverted(ILogger logger, bool encrypted);

    [LoggerMessage(Level = LogLevel.Information, Message = "Budget file key remembered in the secret store: {Remembered}")]
    private static partial void LogRememberChanged(ILogger logger, bool remembered);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The secret store could not read or change the budget file key")]
    private static partial void LogSecretStoreFailed(ILogger logger, Exception exception);
}
