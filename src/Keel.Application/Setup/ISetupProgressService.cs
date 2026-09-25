namespace Keel.Application.Setup;

/// <summary>
/// The Home "Get started" checklist (PRD 9.10 step 4): which setup steps the open budget file already
/// has, computed from the data itself so it stays right whatever path the user took.
/// </summary>
public interface ISetupProgressService
{
    /// <summary>Reads the checklist state.</summary>
    Task<SetupProgress> GetAsync(CancellationToken ct);

    /// <summary>Hides the checklist card for this budget file.</summary>
    Task DismissAsync(CancellationToken ct);
}

/// <summary>Checklist state of a budget file.</summary>
/// <param name="HasCategories">At least one user category exists (template or hand-made).</param>
/// <param name="HasAccounts">At least one account exists.</param>
/// <param name="HasAssignments">Money was assigned to a category at least once.</param>
/// <param name="IsDismissed">The user hid the card.</param>
public sealed record SetupProgress(bool HasCategories, bool HasAccounts, bool HasAssignments, bool IsDismissed)
{
    /// <summary>Completed steps out of four (the budget file itself is step one and always done).</summary>
    public int CompletedSteps => 1 + (HasCategories ? 1 : 0) + (HasAccounts ? 1 : 0) + (HasAssignments ? 1 : 0);

    /// <summary>All four steps are done.</summary>
    public bool IsComplete => CompletedSteps == 4;
}
