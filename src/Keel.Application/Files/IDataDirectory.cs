namespace Keel.Application.Files;

/// <summary>
/// The per-user data directory (PRD 7.4):
/// Windows <c>%APPDATA%\Keel</c>, macOS <c>~/Library/Application Support/Keel</c>,
/// Linux <c>~/.local/share/keel</c>.
/// </summary>
public interface IDataDirectory
{
    /// <summary>Root of the data directory.</summary>
    string Root { get; }

    /// <summary>App-level settings file (<c>settings.json</c>).</summary>
    string SettingsFile { get; }

    /// <summary>Rolling log files.</summary>
    string LogsDirectory { get; }

    /// <summary>Windows DPAPI blobs and the Linux fallback secret file.</summary>
    string SecretsDirectory { get; }

    /// <summary>Default location of budget files.</summary>
    string BudgetsDirectory { get; }

    /// <summary>Backups of budget files.</summary>
    string BackupsDirectory { get; }

    /// <summary>The budget file created on first launch (<c>budgets/Default.keel</c>).</summary>
    string DefaultBudgetFile { get; }

    /// <summary>The attachments folder that sits next to a budget file (<c>Name.keel-attachments</c>).</summary>
    string AttachmentsDirectoryFor(string budgetFile);

    /// <summary>Creates the directory tree if it does not exist.</summary>
    void EnsureCreated();
}
