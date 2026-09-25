namespace Keel.Desktop.Services;

/// <summary>
/// How a session (one host over one budget file) starts: a file passed on the command line, by a
/// second launch, or by Settings → General (open, create, move, restore), and whether the first-run
/// setup continues in it.
/// </summary>
/// <param name="FilePath">Budget file to open or create; null opens the remembered file.</param>
/// <param name="Strict">Fail instead of falling back to the remembered or default file (file switches).</param>
/// <param name="ResumeFirstRun">Continue the first-run setup (template, first account) in the new session.</param>
/// <param name="Message">Status-strip message instead of the default "Opened …".</param>
public sealed record BudgetStartupOptions(string? FilePath = null, bool Strict = false, bool ResumeFirstRun = false, string? Message = null)
{
    /// <summary>Default startup: the remembered file, else the default file, else the first-run setup.</summary>
    public static BudgetStartupOptions Default { get; } = new();
}

/// <summary>Steps of the first-run setup (PRD 9.10).</summary>
public enum FirstRunStep
{
    /// <summary>Create a new budget file or open an existing one.</summary>
    Welcome,

    /// <summary>Choose a starter category template (or empty).</summary>
    Template,

    /// <summary>Add the first account with its balance, or read about bank connections.</summary>
    Account,
}
