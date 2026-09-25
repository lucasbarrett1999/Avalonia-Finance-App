namespace Keel.Application.Backup;

/// <summary>
/// Backups of the open budget file and its attachments (F-SET-1, PRD 10): timestamped zips in the data
/// directory's <c>budgets/backups</c> folder, each verified by reopening the copied database.
/// </summary>
public interface IBackupService
{
    /// <summary>Copies the current budget file and attachments to a timestamped zip and verifies it.</summary>
    Task<BackupInfo> BackupNowAsync(CancellationToken ct);

    /// <summary>Creates and verifies a backup of the given kind.</summary>
    /// <exception cref="BackupVerificationException">The copy could not be reopened; it is deleted.</exception>
    Task<BackupInfo> BackupAsync(BackupKind kind, CancellationToken ct);

    /// <summary>
    /// The automatic daily backup: creates one when the current file has no automatic backup dated
    /// <paramref name="today"/>, then keeps the newest <paramref name="keep"/> automatic backups.
    /// Returns the new backup, or null when today's already exists.
    /// </summary>
    Task<BackupInfo?> RunDailyBackupAsync(DateOnly today, int keep, CancellationToken ct);

    /// <summary>Lists backups of the current budget file, newest first.</summary>
    Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(CancellationToken ct);

    /// <summary>Verifies a backup by reopening the copied database (integrity check and known schema).</summary>
    Task<bool> VerifyAsync(string backupPath, CancellationToken ct);

    /// <summary>
    /// Restores a backup over the current budget file (after the UI confirms): verifies it, takes a
    /// "before restore" backup of the current state, then copies the database and attachments back.
    /// The caller reopens the file afterwards so every screen reloads.
    /// </summary>
    /// <exception cref="BackupVerificationException">The backup is damaged or from a newer version of Keel.</exception>
    /// <exception cref="Files.BudgetFileLockedException">The backup is encrypted with another passphrase than the open file.</exception>
    Task RestoreAsync(string backupPath, CancellationToken ct);

    /// <summary>
    /// Restores a backup that is encrypted with <paramref name="backupPassphrase"/> (F-SET-4). The restored file
    /// keeps the open file's encryption: the backup's content is converted to it when the two differ.
    /// </summary>
    /// <exception cref="Files.BudgetFileLockedException">The passphrase does not open the backup.</exception>
    Task RestoreAsync(string backupPath, string? backupPassphrase, CancellationToken ct) => RestoreAsync(backupPath, ct);

    /// <summary>Deletes all but the newest <paramref name="keep"/> automatic backups.</summary>
    Task PruneAsync(int keep, CancellationToken ct);
}

/// <summary>Why a backup was taken (part of its file name).</summary>
public enum BackupKind
{
    /// <summary>"Backup now" (<c>Name-YYYYMMDD-HHMMSS.zip</c>, PRD 7.4).</summary>
    Manual,

    /// <summary>The automatic daily backup (<c>-auto</c>); the only kind pruned by keep-N.</summary>
    Automatic,

    /// <summary>Taken before applying schema migrations (PRD 11; <c>-before-migration</c>).</summary>
    BeforeMigration,

    /// <summary>Taken before a restore replaced the file (<c>-before-restore</c>).</summary>
    BeforeRestore,

    /// <summary>Taken before the file was encrypted (F-SET-4; <c>-before-encryption</c>, a plain copy).</summary>
    BeforeEncryption,

    /// <summary>Taken before the encryption was removed (<c>-before-decryption</c>, still encrypted).</summary>
    BeforeDecryption,
}

/// <summary>A backup file.</summary>
/// <param name="Path">Full path of the zip.</param>
/// <param name="CreatedAt">When it was taken (from the file name, local time).</param>
/// <param name="SizeBytes">Zip size.</param>
/// <param name="Kind">Why it was taken.</param>
public sealed record BackupInfo(string Path, DateTimeOffset CreatedAt, long SizeBytes, BackupKind Kind = BackupKind.Manual)
{
    /// <summary>File name without directory.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>A backup could not be verified (damaged, not a Keel backup, or from a newer version).</summary>
public sealed class BackupVerificationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public BackupVerificationException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public BackupVerificationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public BackupVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
