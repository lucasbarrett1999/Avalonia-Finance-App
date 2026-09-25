using Keel.Application.Files;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Files;

/// <summary>
/// Opens or creates a budget file and brings its schema up to date. With a data directory it first
/// backs up an existing file that needs migrations (PRD 11: auto-backup before every migration).
/// </summary>
public sealed partial class BudgetFileService(KeelDbContextFactory factory, ILogger<BudgetFileService> logger, IDataDirectory? dataDirectory = null, TimeProvider? time = null) : IBudgetFileService
{
    /// <inheritdoc />
    public string? CurrentPath => factory.CurrentPath;

    /// <inheritdoc />
    /// <exception cref="BudgetFileTooNewException">The file was written by a newer version of Keel.</exception>
    public async Task<BudgetFileInfo> OpenOrCreateAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var created = !File.Exists(fullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        List<string> pending;
        var db = KeelDbContextFactory.CreateForFile(fullPath);
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
                await Task.Run(() => BackupArchive.Write(fullPath, dataDirectory.AttachmentsDirectoryFor(fullPath), zip, Keel.Application.Backup.BackupKind.BeforeMigration, clock.GetUtcNow().UtcDateTime), ct).ConfigureAwait(false);
                LogPreMigrationBackup(logger);
            }

            if (pending.Count > 0)
            {
                LogMigrating(logger, pending.Count);
                await db.Database.MigrateAsync(ct).ConfigureAwait(false);
            }
        }

        factory.UseFile(fullPath);
        LogOpened(logger, created, pending.Count);
        return new BudgetFileInfo(fullPath, created, pending);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backed up the budget file before migrating it")]
    private static partial void LogPreMigrationBackup(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} pending migration(s) to the budget file")]
    private static partial void LogMigrating(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Budget file opened (created: {Created}, migrations applied: {Applied})")]
    private static partial void LogOpened(ILogger logger, bool created, int applied);
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
