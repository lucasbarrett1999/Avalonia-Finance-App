namespace Keel.Application.Files;

/// <summary>Opens and creates <c>.keel</c> budget files (SQLite databases).</summary>
public interface IBudgetFileService
{
    /// <summary>File extension of budget files, including the dot.</summary>
    const string Extension = ".keel";

    /// <summary>Full path of the open budget file, or null before one is opened.</summary>
    string? CurrentPath { get; }

    /// <summary>
    /// Opens the budget file at <paramref name="path"/>, creating it if missing, and applies
    /// pending migrations. Afterwards all database access goes to this file.
    /// </summary>
    Task<BudgetFileInfo> OpenOrCreateAsync(string path, CancellationToken ct);
}

/// <summary>Result of opening a budget file.</summary>
/// <param name="Path">Full path.</param>
/// <param name="Created">Whether the file was created by this call.</param>
/// <param name="AppliedMigrations">Migrations applied by this call.</param>
public sealed record BudgetFileInfo(string Path, bool Created, IReadOnlyList<string> AppliedMigrations)
{
    /// <summary>File name without directory, e.g. "Default.keel".</summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}
