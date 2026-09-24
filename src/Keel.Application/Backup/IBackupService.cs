namespace Keel.Application.Backup;

/// <summary>Backups of the budget file and its attachments (F-SET-1). Implemented in M8.</summary>
public interface IBackupService
{
    /// <summary>Copies the current budget file and attachments to a timestamped zip and verifies it.</summary>
    Task<BackupInfo> BackupNowAsync(CancellationToken ct);

    /// <summary>Lists backups of the current budget file, newest first.</summary>
    Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(CancellationToken ct);

    /// <summary>Verifies a backup by reopening the copied database.</summary>
    Task<bool> VerifyAsync(string backupPath, CancellationToken ct);

    /// <summary>Restores a backup over the current budget file (after the UI confirms).</summary>
    Task RestoreAsync(string backupPath, CancellationToken ct);

    /// <summary>Deletes all but the newest <paramref name="keep"/> automatic backups.</summary>
    Task PruneAsync(int keep, CancellationToken ct);
}

/// <summary>A backup file.</summary>
public sealed record BackupInfo(string Path, DateTimeOffset CreatedAt, long SizeBytes);
