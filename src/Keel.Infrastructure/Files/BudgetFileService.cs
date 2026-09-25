using Keel.Application.Files;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Files;

/// <summary>
/// Opens or creates a budget file and brings its schema up to date. With a data directory it first
/// backs up an existing file that needs migrations (PRD 11: auto-backup before every migration). An
/// encrypted file (F-SET-4, ADR 0101) opens with the passphrase given, a key unlocked earlier in this app
/// run (<see cref="BudgetFileKeyRing"/>), or a key the user chose to remember in the OS secret store.
/// </summary>
public sealed partial class BudgetFileService(
    KeelDbContextFactory factory,
    ILogger<BudgetFileService> logger,
    IDataDirectory? dataDirectory = null,
    TimeProvider? time = null,
    ISecretStore? secrets = null,
    BudgetFileKeyRing? keyRing = null) : IBudgetFileService
{
    private readonly BudgetFileKeyRing _keyRing = keyRing ?? new BudgetFileKeyRing();

    /// <inheritdoc />
    public string? CurrentPath => factory.CurrentPath;

    /// <inheritdoc />
    /// <exception cref="BudgetFileTooNewException">The file was written by a newer version of Keel.</exception>
    public Task<BudgetFileInfo> OpenOrCreateAsync(string path, CancellationToken ct) => OpenOrCreateAsync(path, null, ct);

    /// <inheritdoc />
    /// <exception cref="BudgetFileTooNewException">The file was written by a newer version of Keel.</exception>
    public async Task<BudgetFileInfo> OpenOrCreateAsync(string path, BudgetFileUnlock? unlock, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var created = !File.Exists(fullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var fileId = SqlCipher.FileId(fullPath);
        var key = fileId is null ? null : await ResolveKeyAsync(fullPath, fileId, unlock, ct).ConfigureAwait(false);

        List<string> pending;
        var db = KeelDbContextFactory.CreateForFile(fullPath, key);
        await using (db.ConfigureAwait(false))
        {
            var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            var applied = await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false);
            var unknown = applied.Where(m => !known.Contains(m)).ToList();
            if (unknown.Count > 0)
            {
                throw new BudgetFileTooNewException(fullPath, unknown);
            }

            pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            if (pending.Count > 0 && applied.Any() && dataDirectory is not null)
            {
                var clock = time ?? TimeProvider.System;
                var zip = BackupPaths.UniqueZipPath(dataDirectory, fullPath, Keel.Application.Backup.BackupKind.BeforeMigration, clock.GetLocalNow().DateTime);
                await Task.Run(() => BackupArchive.Write(fullPath, dataDirectory.AttachmentsDirectoryFor(fullPath), zip, Keel.Application.Backup.BackupKind.BeforeMigration, clock.GetUtcNow().UtcDateTime, key), ct).ConfigureAwait(false);
                LogPreMigrationBackup(logger);
            }

            if (pending.Count > 0)
            {
                LogMigrating(logger, pending.Count);
                await db.Database.MigrateAsync(ct).ConfigureAwait(false);
            }
        }

        factory.UseFile(fullPath, key);
        if (fileId is not null && key is not null)
        {
            _keyRing.Remember(fileId, key);
            if (unlock?.Remember == true)
            {
                await RememberKeyAsync(fileId, key).ConfigureAwait(false);
            }
        }

        LogOpened(logger, created, pending.Count, key is not null);
        return new BudgetFileInfo(fullPath, created, pending) { IsEncrypted = key is not null };
    }

    // The typed passphrase wins; otherwise a key unlocked earlier in this run, then the secret store.
    private async Task<string> ResolveKeyAsync(string path, string fileId, BudgetFileUnlock? unlock, CancellationToken ct)
    {
        if (unlock is not null)
        {
            var typed = await Task.Run(() => SqlCipher.KeyForFile(path, unlock.Passphrase), ct).ConfigureAwait(false);
            return SqlCipher.CanOpen(path, typed) ? typed : throw new BudgetFileLockedException(path, wrongPassphrase: true);
        }

        if (_keyRing.Find(fileId) is { } unlocked && SqlCipher.CanOpen(path, unlocked))
        {
            return unlocked;
        }

        if (secrets is not null)
        {
            try
            {
                if (await secrets.GetAsync(SecretKeys.BudgetFile(fileId)).ConfigureAwait(false) is { Length: > 0 } remembered && SqlCipher.CanOpen(path, remembered))
                {
                    LogRememberedKeyUsed(logger);
                    return remembered;
                }
            }
            catch (SecretStoreException ex)
            {
                LogSecretStoreFailed(logger, ex);
            }
        }

        throw new BudgetFileLockedException(path, wrongPassphrase: false);
    }

    private async Task RememberKeyAsync(string fileId, string key)
    {
        if (secrets is null)
        {
            return;
        }

        try
        {
            await secrets.SetAsync(SecretKeys.BudgetFile(fileId), key).ConfigureAwait(false);
        }
        catch (SecretStoreException ex)
        {
            // The file is open; only the "remember" part failed. Settings shows the state.
            LogSecretStoreFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened the encrypted budget file with the key remembered in the secret store")]
    private static partial void LogRememberedKeyUsed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The secret store could not read or keep the budget file key")]
    private static partial void LogSecretStoreFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backed up the budget file before migrating it")]
    private static partial void LogPreMigrationBackup(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} pending migration(s) to the budget file")]
    private static partial void LogMigrating(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Budget file opened (created: {Created}, migrations applied: {Applied}, encrypted: {Encrypted})")]
    private static partial void LogOpened(ILogger logger, bool created, int applied, bool encrypted);
}

/// <summary>Thrown when a budget file contains migrations this version of Keel does not know.</summary>
public sealed class BudgetFileTooNewException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public BudgetFileTooNewException(string path, IReadOnlyList<string> unknownMigrations)
        : base($"The budget file '{Path.GetFileName(path)}' was created by a newer version of Keel. Update Keel to open it.")
    {
        UnknownMigrations = unknownMigrations;
    }

    /// <summary>Creates the exception.</summary>
    public BudgetFileTooNewException()
        : this(string.Empty, [])
    {
    }

    /// <summary>Creates the exception.</summary>
    public BudgetFileTooNewException(string message)
        : base(message)
    {
        UnknownMigrations = [];
    }

    /// <summary>Creates the exception.</summary>
    public BudgetFileTooNewException(string message, Exception innerException)
        : base(message, innerException)
    {
        UnknownMigrations = [];
    }

    /// <summary>Migration ids in the file that this build does not contain.</summary>
    public IReadOnlyList<string> UnknownMigrations { get; }
}
