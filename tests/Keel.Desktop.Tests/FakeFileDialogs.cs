using Keel.Desktop.Services;

namespace Keel.Desktop.Tests;

/// <summary>Answers the budget-file and backup pickers with queued paths (null = cancelled).</summary>
internal sealed class FakeFileDialogs : IFileDialogs
{
    public Queue<string?> OpenBudgetFiles { get; } = new();

    public Queue<string?> SaveBudgetFiles { get; } = new();

    public Queue<string?> OpenBackups { get; } = new();

    public Task<string?> OpenBudgetFileAsync(string? startFolder) => Task.FromResult(OpenBudgetFiles.Count > 0 ? OpenBudgetFiles.Dequeue() : null);

    public Task<string?> SaveBudgetFileAsync(string title, string suggestedName, string? startFolder) => Task.FromResult(SaveBudgetFiles.Count > 0 ? SaveBudgetFiles.Dequeue() : null);

    public Task<string?> OpenBackupAsync(string? startFolder) => Task.FromResult(OpenBackups.Count > 0 ? OpenBackups.Dequeue() : null);
}
