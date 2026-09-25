using Keel.Application.Backup;
using Keel.Application.Files;

using Keel.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Files;

/// <summary>
/// Backups of the open budget file (F-SET-1): zips in <c>budgets/backups</c> named after the file and
/// the local time, verified by reopening the copy (PRD 10). Restore copies the database back with the
/// SQLite backup API, so it works while the app holds connections; the app reopens the file afterwards.
/// Backups of an encrypted file stay encrypted with its key (F-SET-4, ADR 0101).
/// </summary>
public sealed partial class BackupService(
    KeelDbContextFactory factory,
    IDataDirectory dataDirectory,
    TimeProvider time,
    ILogger<BackupService> logger) : IBackupService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public Task<BackupInfo> BackupNowAsync(CancellationToken ct) => BackupAsync(BackupKind.Manual, ct);

    /// <inheritdoc />
    public async Task<BackupInfo> BackupAsync(BackupKind kind, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Create(CurrentFile(), kind), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<BackupInfo?> RunDailyBackupAsync(DateOnly today, int keep, CancellationToken ct)
    {
        var existing = await ListBackupsAsync(ct).ConfigureAwait(false);
        if (existing.Any(b => b.Kind == BackupKind.Automatic && DateOnly.FromDateTime(b.CreatedAt.DateTime) == today))
        {
            return null;
        }

        var created = await BackupAsync(BackupKind.Automatic, ct).ConfigureAwait(false);
        await PruneAsync(keep, ct).ConfigureAwait(false);
        return created;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(CancellationToken ct)
    {
        var file = factory.CurrentPath;
        if (file is null || !Directory.Exists(dataDirectory.BackupsDirectory))
        {
            return Task.FromResult<IReadOnlyList<BackupInfo>>([]);
        }

        IReadOnlyList<BackupInfo> list = Directory.EnumerateFiles(dataDirectory.BackupsDirectory, "*.zip")
            .Select(path => (Path: path, Parsed: BackupArchive.Parse(file, Path.GetFileName(path))))
            .Where(x => x.Parsed is not null)
            .Select(x => new BackupInfo(x.Path, new DateTimeOffset(x.Parsed!.Value.LocalTime), new FileInfo(x.Path).Length, x.Parsed.Value.Kind))
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Path, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(list);
    }

    /// <inheritdoc />
    public Task<bool> VerifyAsync(string backupPath, CancellationToken ct) => Task.Run(
        () =>
        {
            var work = WorkDirectory();
            try
            {
                BackupArchive.ExtractAndVerify(backupPath, work, factory.CurrentKey);
                return true;
            }
            catch (Exception ex) when (ex is BackupVerificationException or BudgetFileLockedException)
            {
                LogVerifyFailed(logger, ex);
                return false;
            }
            finally
            {
                BackupArchive.TryDeleteDirectory(work);
            }
        },
        ct);

    /// <inheritdoc />
    public Task RestoreAsync(string backupPath, CancellationToken ct) => RestoreAsync(backupPath, null, ct);

    /// <inheritdoc />
    public async Task RestoreAsync(string backupPath, string? backupPassphrase, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(
                () =>
                {
                    var target = CurrentFile();
                    var targetKey = factory.CurrentKey;
                    var work = WorkDirectory();
                    try
                    {
                        var (extracted, backupKey) = BackupArchive.ExtractAndVerifyWithKey(backupPath, work, [targetKey], backupPassphrase);
                        Create(target, BackupKind.BeforeRestore);

                        // The restored file keeps the open file's encryption: the SQLite backup API cannot copy
                        // between a plain and an encrypted database or across keys, so convert the copy first.
                        if (!string.Equals(backupKey, targetKey, StringComparison.Ordinal))
                        {
                            var converted = Path.Combine(work, "converted" + Path.GetExtension(extracted));
                            SqlCipher.Export(extracted, backupKey, converted, targetKey);
                            extracted = converted;
                        }

                        using (var source = new SqliteConnection(BackupArchive.UnpooledConnectionString(extracted, targetKey, SqliteOpenMode.ReadOnly)))
                        using (var destination = new SqliteConnection(BackupArchive.UnpooledConnectionString(target, targetKey, SqliteOpenMode.ReadWrite)))
                        {
                            source.Open();
                            destination.Open();
                            BackupArchive.SetBusyTimeout(destination);
                            source.BackupDatabase(destination);
                        }

                        SqliteConnection.ClearAllPools();
                        BackupArchive.ExtractAttachments(backupPath, dataDirectory.AttachmentsDirectoryFor(target));
                        LogRestored(logger);
                    }
                    finally
                    {
                        BackupArchive.TryDeleteDirectory(work);
                    }
                },
                ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task PruneAsync(int keep, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keep);
        var automatic = (await ListBackupsAsync(ct).ConfigureAwait(false)).Where(b => b.Kind == BackupKind.Automatic).ToList();
        foreach (var old in automatic.Skip(keep))
        {
            try
            {
                File.Delete(old.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogPruneFailed(logger, ex);
            }
        }
    }

    private BackupInfo Create(string budgetFile, BackupKind kind)
    {
        var now = time.GetLocalNow().DateTime;
        var zip = BackupPaths.UniqueZipPath(dataDirectory, budgetFile, kind, now);
        var key = factory.CurrentKey;
        BackupArchive.Write(budgetFile, dataDirectory.AttachmentsDirectoryFor(budgetFile), zip, kind, time.GetUtcNow().UtcDateTime, key);

        var work = WorkDirectory();
        try
        {
            BackupArchive.ExtractAndVerify(zip, work, key);
        }
        catch (Exception ex) when (ex is BackupVerificationException or BudgetFileLockedException)
        {
            File.Delete(zip);
            throw;
        }
        finally
        {
            BackupArchive.TryDeleteDirectory(work);
        }

        LogCreated(logger, kind);
        return new BackupInfo(zip, new DateTimeOffset(TruncateToSeconds(now)), new FileInfo(zip).Length, kind);
    }

    private static DateTime TruncateToSeconds(DateTime value) => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Kind);

    private string CurrentFile() => factory.CurrentPath ?? throw new InvalidOperationException("No budget file is open.");

    private string WorkDirectory() => Path.Combine(dataDirectory.BackupsDirectory, ".tmp-" + Guid.NewGuid().ToString("N"));

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup created ({Kind})")]
    private static partial void LogCreated(ILogger logger, BackupKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup restored over the open budget file")]
    private static partial void LogRestored(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup verification failed")]
    private static partial void LogVerifyFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting an old automatic backup failed")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);
}
