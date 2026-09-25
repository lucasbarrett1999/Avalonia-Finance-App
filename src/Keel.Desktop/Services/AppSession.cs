using Keel.Application.Files;

namespace Keel.Desktop.Services;

/// <summary>State of the running session: which budget file is open and how startup went.</summary>
public sealed class AppSession
{
    /// <summary>The open budget file, or null when none could be opened.</summary>
    public BudgetFileInfo? BudgetFile { get; set; }

    /// <summary>Status-strip message describing how startup went.</summary>
    public string? StartupMessage { get; set; }

    /// <summary>The first-run setup step to show, or null when the setup is not running (PRD 9.10).</summary>
    public FirstRunStep? FirstRun { get; set; }

    /// <summary>An encrypted budget file waiting for its passphrase (F-SET-4); no file is open meanwhile.</summary>
    public string? LockedFile { get; set; }
}
