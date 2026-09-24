using System.Text;

namespace Keel.Domain.Rules;

/// <summary>Fields a rule run changed.</summary>
[Flags]
public enum RuleChanges
{
    /// <summary>Nothing changed.</summary>
    None = 0,

    /// <summary>Payee name.</summary>
    Payee = 1,

    /// <summary>Category (set, or cleared because the transaction was split).</summary>
    Category = 2,

    /// <summary>Memo.</summary>
    Memo = 4,

    /// <summary>Tags were added.</summary>
    Tags = 8,

    /// <summary>Marked approved.</summary>
    Approved = 16,

    /// <summary>Flagged.</summary>
    Flagged = 32,

    /// <summary>Splits were created or removed.</summary>
    Splits = 64,

    /// <summary>Transfer account.</summary>
    TransferAccount = 128,
}

/// <summary>
/// The mutation set of a rule run: the resulting snapshot, which fields differ from the input,
/// and which rule last set each field. Applying it means copying the <see cref="Changes"/>
/// fields of <see cref="Result"/> onto the transaction.
/// </summary>
public sealed record RuleMutations
{
    /// <summary>The transaction after every matched rule's actions.</summary>
    public required TransactionSnapshot Result { get; init; }

    /// <summary>Fields that differ from the input snapshot.</summary>
    public RuleChanges Changes { get; init; }

    /// <summary>Tags added (in the order they were added).</summary>
    public IReadOnlyList<string> AddedTags { get; init; } = [];

    /// <summary>For each changed field (single flag), the id of the rule whose action set it last.</summary>
    public IReadOnlyDictionary<RuleChanges, Guid> SetBy { get; init; } = new Dictionary<RuleChanges, Guid>();

    /// <summary>True when any field changed.</summary>
    public bool HasChanges => Changes != RuleChanges.None;

    /// <summary>True when a rule decided the category: set one, split the transaction, or made it a transfer.</summary>
    public bool DecidesCategory =>
        (Changes & (RuleChanges.Category | RuleChanges.Splits | RuleChanges.TransferAccount)) != 0
        && (Result.CategoryId is not null || Result.IsSplit || Result.TransferAccountId is not null);

    /// <summary>True when <paramref name="field"/> changed.</summary>
    public bool Has(RuleChanges field) => (Changes & field) == field && field != RuleChanges.None;
}

/// <summary>What happened to one rule during a run.</summary>
public enum RuleOutcome
{
    /// <summary>Conditions matched; actions ran.</summary>
    Matched,

    /// <summary>Conditions did not match.</summary>
    NotMatched,

    /// <summary>The rule is disabled and was skipped.</summary>
    Disabled,

    /// <summary>The rule has validation errors and was skipped.</summary>
    Invalid,
}

/// <summary>The result of one condition.</summary>
/// <param name="Index">Zero-based position in the rule.</param>
/// <param name="Description">English description of the condition.</param>
/// <param name="Matched">Whether it matched.</param>
/// <param name="Note">Extra information, e.g. a regular-expression timeout.</param>
public sealed record ConditionResult(int Index, string Description, bool Matched, string? Note = null);

/// <summary>The result of one action.</summary>
/// <param name="Index">Zero-based position in the rule.</param>
/// <param name="Description">English description of the action.</param>
/// <param name="Applied">Whether it changed anything.</param>
/// <param name="Note">Why it did not apply, when it did not.</param>
public sealed record ActionResult(int Index, string Description, bool Applied, string? Note = null);

/// <summary>One rule's evaluation in a run.</summary>
/// <param name="RuleId">Rule id.</param>
/// <param name="RuleName">Rule name.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Conditions">Every condition's result (empty for disabled and invalid rules).</param>
/// <param name="Actions">Every action's result (empty unless matched).</param>
/// <param name="Problems">Validation errors for an invalid rule.</param>
/// <param name="StoppedEvaluation">True when this rule matched without "continue", so later rules were not evaluated.</param>
public sealed record RuleEvaluation(
    Guid RuleId,
    string RuleName,
    RuleOutcome Outcome,
    IReadOnlyList<ConditionResult> Conditions,
    IReadOnlyList<ActionResult> Actions,
    IReadOnlyList<string> Problems,
    bool StoppedEvaluation);

/// <summary>
/// The trace of a rule run (principle 7): every rule considered, in evaluation order, up to the
/// rule that stopped evaluation. Rules after that are not listed.
/// </summary>
/// <param name="Evaluations">Evaluations in order.</param>
public sealed record RuleTrace(IReadOnlyList<RuleEvaluation> Evaluations)
{
    /// <summary>The rules that matched, in order.</summary>
    public IReadOnlyList<RuleEvaluation> Matched => Evaluations.Where(e => e.Outcome == RuleOutcome.Matched).ToList();

    /// <summary>The rule that stopped evaluation (first match without "continue"), if any.</summary>
    public Guid? StoppedByRuleId => Evaluations.FirstOrDefault(e => e.StoppedEvaluation)?.RuleId;

    /// <summary>A multi-line English account of the run, for logs-free display and tests.</summary>
    public string Describe()
    {
        if (Evaluations.Count == 0)
        {
            return "No rules.";
        }

        var sb = new StringBuilder();
        foreach (var e in Evaluations)
        {
            sb.Append('\'').Append(e.RuleName).Append("': ");
            switch (e.Outcome)
            {
                case RuleOutcome.Disabled:
                    sb.Append("skipped (disabled)");
                    break;
                case RuleOutcome.Invalid:
                    sb.Append("skipped (invalid: ").Append(string.Join(" ", e.Problems)).Append(')');
                    break;
                default:
                    sb.Append(e.Outcome == RuleOutcome.Matched ? "matched" : "did not match");
                    sb.Append(" [").Append(string.Join("; ", e.Conditions.Select(c =>
                        c.Description + (c.Matched ? " = yes" : " = no") + (c.Note is null ? string.Empty : " (" + c.Note + ")")))).Append(']');
                    if (e.Actions.Count > 0)
                    {
                        sb.Append(" -> ").Append(string.Join("; ", e.Actions.Select(a =>
                            a.Description + (a.Applied ? string.Empty : " (not applied: " + a.Note + ")"))));
                    }

                    if (e.StoppedEvaluation)
                    {
                        sb.Append("; stopped");
                    }

                    break;
            }

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }
}

/// <summary>The output of <see cref="RuleEngine"/>: the mutation set and the trace.</summary>
/// <param name="Original">The input snapshot.</param>
/// <param name="Mutations">What changed.</param>
/// <param name="Trace">How it was decided.</param>
public sealed record RuleApplication(TransactionSnapshot Original, RuleMutations Mutations, RuleTrace Trace)
{
    /// <summary>The transaction after the rules.</summary>
    public TransactionSnapshot Result => Mutations.Result;
}
