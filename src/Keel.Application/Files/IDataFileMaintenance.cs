namespace Keel.Application.Files;

/// <summary>
/// Care of the budget file itself (F-SET-1, PRD 10): the once-a-day <c>PRAGMA integrity_check</c>,
/// consistent copies for "Change location", deleting a moved file, and the schema-only diagnostic bundle.
/// </summary>
public interface IDataFileMaintenance
{
    /// <summary>Runs <c>PRAGMA integrity_check</c> on the open budget file and records the day.</summary>
    Task<IntegrityCheckResult> CheckIntegrityAsync(CancellationToken ct);

    /// <summary>Runs the check unless it already ran on <paramref name="today"/> for this file; null when skipped.</summary>
    Task<IntegrityCheckResult?> CheckIntegrityIfDueAsync(DateOnly today, CancellationToken ct);

    /// <summary>
    /// Writes a consistent copy of the open budget file (SQLite backup API, so it is safe while the
    /// app runs) and its attachments folder to <paramref name="destinationPath"/>, then verifies the copy.
    /// </summary>
    /// <exception cref="IOException">The destination exists or cannot be written.</exception>
    Task CopyToAsync(string destinationPath, CancellationToken ct);

    /// <summary>Deletes a budget file that is no longer open: the file, its WAL/SHM files and attachments folder.</summary>
    Task DeleteBudgetFileAsync(string path, CancellationToken ct);

    /// <summary>
    /// Writes <c>keel-diagnostics-YYYYMMDD-HHMMSS.zip</c> into <paramref name="destinationDirectory"/>:
    /// the log files plus a schema-only summary of the open file (tables, columns, indexes, migrations,
    /// row counts and SQLite settings; never rows, payees or amounts). Returns the zip path.
    /// </summary>
    Task<string> CreateDiagnosticBundleAsync(string destinationDirectory, string appVersion, CancellationToken ct);
}

/// <summary>Outcome of <c>PRAGMA integrity_check</c>.</summary>
/// <param name="IsOk">SQLite answered "ok".</param>
/// <param name="Problems">SQLite's messages when not ok (at most 20).</param>
/// <param name="CheckedAt">When the check ran (UTC).</param>
public sealed record IntegrityCheckResult(bool IsOk, IReadOnlyList<string> Problems, DateTime CheckedAt);
