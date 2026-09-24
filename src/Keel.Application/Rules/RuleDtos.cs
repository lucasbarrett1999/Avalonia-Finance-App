using Keel.Domain.Rules;

namespace Keel.Application.Rules;

/// <summary>A stored rule for the rules list and editor.</summary>
/// <param name="Id">Rule id.</param>
/// <param name="Name">Name.</param>
/// <param name="SortOrder">Evaluation order.</param>
/// <param name="IsEnabled">Enabled.</param>
/// <param name="ContinueAfterMatch">Keep evaluating after a match.</param>
/// <param name="Definition">The parsed rule, or null when its JSON cannot be read by this version.</param>
/// <param name="Summary">English one-line summary of conditions and actions (names resolved).</param>
/// <param name="FormatError">Why the rule cannot be read, when <paramref name="Definition"/> is null.</param>
/// <param name="Problems">Validation errors and warnings.</param>
public sealed record RuleDto(
    Guid Id,
    string Name,
    int SortOrder,
    bool IsEnabled,
    bool ContinueAfterMatch,
    RuleDefinition? Definition,
    string Summary,
    string? FormatError,
    IReadOnlyList<RuleProblem> Problems)
{
    /// <summary>Whether this app version can read the rule.</summary>
    public bool IsReadable => Definition is not null;

    /// <summary>Whether the rule has validation errors (the engine skips it).</summary>
    public bool HasErrors => Problems.Any(p => p.Severity == RuleProblemSeverity.Error);
}

/// <summary>Which existing transactions a retroactive apply looks at.</summary>
/// <param name="From">First date, inclusive.</param>
/// <param name="To">Last date, inclusive.</param>
/// <param name="AccountId">One account, or null for all.</param>
/// <param name="UnapprovedOnly">Only transactions still waiting in the review queue.</param>
public sealed record RetroactiveScope(DateOnly? From = null, DateOnly? To = null, Guid? AccountId = null, bool UnapprovedOnly = false)
{
    /// <summary>Every non-deleted transaction.</summary>
    public static RetroactiveScope All { get; } = new();
}

/// <summary>One field a rule changes on a transaction, as display values (names, memo text).</summary>
/// <param name="Field">The field (a single <see cref="RuleChanges"/> flag).</param>
/// <param name="Before">Value before; null for none (no category, no memo, ...).</param>
/// <param name="After">Value after; null for none.</param>
public sealed record FieldChange(RuleChanges Field, string? Before, string? After);

/// <summary>A transaction and what rules do (or would do) to it.</summary>
/// <param name="TransactionId">Transaction.</param>
/// <param name="AccountName">Account name.</param>
/// <param name="Date">Date.</param>
/// <param name="Payee">Current payee name (or descriptor).</param>
/// <param name="Amount">Signed minor units.</param>
/// <param name="Currency">Currency of the amount.</param>
/// <param name="Changes">Fields that change (or would); <see cref="RuleChanges.None"/> when the rule matches without changing anything.</param>
/// <param name="FieldChanges">Each changed field with before and after values, in <see cref="RuleChanges"/> order.</param>
/// <param name="RuleNames">Rules that matched, in order.</param>
public sealed record RuleOutcomePreview(
    Guid TransactionId,
    string AccountName,
    DateOnly Date,
    string Payee,
    long Amount,
    string Currency,
    RuleChanges Changes,
    IReadOnlyList<FieldChange> FieldChanges,
    IReadOnlyList<string> RuleNames);

/// <summary>What a retroactive apply would change.</summary>
/// <param name="Examined">Transactions in scope.</param>
/// <param name="Changes">Transactions that would change, oldest first.</param>
public sealed record RetroactivePreview(int Examined, IReadOnlyList<RuleOutcomePreview> Changes);

/// <summary>What a retroactive apply changed.</summary>
/// <param name="Examined">Transactions in scope.</param>
/// <param name="Changed">Transactions changed.</param>
/// <param name="Changes">The changes written (same shape as the preview).</param>
public sealed record RetroactiveResult(int Examined, int Changed, IReadOnlyList<RuleOutcomePreview> Changes);

/// <summary>"Test rule" result.</summary>
/// <param name="Examined">Transactions examined.</param>
/// <param name="Matched">Transactions whose conditions match.</param>
/// <param name="Samples">Up to the requested number of matches, newest first.</param>
public sealed record RuleTestResult(int Examined, int Matched, IReadOnlyList<RuleOutcomePreview> Samples);

/// <summary>Stored rules changed (created, edited, reordered, enabled, deleted); rule views refresh.</summary>
public sealed record RulesChanged;
