using Keel.Domain.Categorization;
using Keel.Domain.Rules;

namespace Keel.Application.Categorization;

/// <summary>
/// The categorization step of the unified import pipeline (F-TXN-1 step 4) and the source of the
/// review queue's suggestions (F-TXN-6). Pure: no persistence, no clock. The order is:
/// <list type="number">
/// <item>Rules (F-TXN-4). If a rule sets a category, splits the transaction or makes it a
/// transfer, that decides it and the learner is skipped.</item>
/// <item>The payee's default category (F-TXN-9), suggested with confidence
/// <see cref="CategorizationEngine.PayeeDefaultConfidence"/>.</item>
/// <item>The learner (F-TXN-5); its primary suggestion is at least 60% confident, or there is none.</item>
/// </list>
/// A transaction that already has a category, splits or a transfer account keeps it.
/// </summary>
public interface ICategorizationEngine
{
    /// <summary>Categorizes one transaction with rules compiled once for a batch.</summary>
    /// <param name="snapshot">The transaction (normally new and uncategorized).</param>
    /// <param name="rules">Enabled and disabled rules, compiled by <see cref="RuleEngine.Compile"/>.</param>
    /// <param name="model">The learner model, or null when there is none yet.</param>
    /// <param name="payeeDefault">The resolved payee's default category, if it has one.</param>
    /// <param name="topN">Most suggestions to return (the review screen shows up to 5).</param>
    CategorizationResult Categorize(TransactionSnapshot snapshot, CompiledRuleSet rules, LearnerModel? model, PayeeDefaultCategory? payeeDefault, int topN = 5);

    /// <summary>Categorizes one transaction; compiles <paramref name="rules"/> first.</summary>
    CategorizationResult Categorize(TransactionSnapshot snapshot, IEnumerable<RuleDefinition> rules, LearnerModel? model, PayeeDefaultCategory? payeeDefault, int topN = 5);
}

/// <summary>A payee's default category (F-TXN-9).</summary>
/// <param name="CategoryId">The category.</param>
/// <param name="CategoryName">Its display name, for the explanation.</param>
public sealed record PayeeDefaultCategory(Guid CategoryId, string CategoryName);

/// <summary>Where the category decision came from.</summary>
public enum CategorizationSource
{
    /// <summary>Nothing decided it; the transaction stays uncategorized.</summary>
    None,

    /// <summary>The transaction already had a category, splits or a transfer account.</summary>
    Existing,

    /// <summary>A rule set the category, split the transaction or made it a transfer.</summary>
    Rule,

    /// <summary>The payee's default category.</summary>
    PayeeDefault,

    /// <summary>The learner's primary suggestion.</summary>
    Learner,
}

/// <summary>One suggested category for the review queue, best first.</summary>
/// <param name="CategoryId">Category.</param>
/// <param name="CategoryName">Display name.</param>
/// <param name="Confidence">In [0, 1]; 1 for a rule.</param>
/// <param name="Source">Rule, payee default or learner.</param>
/// <param name="Explanation">English explanation shown next to the suggestion.</param>
/// <param name="IsPrimary">True for the one suggestion that is (or would be) written.</param>
public sealed record CategorizationSuggestion(
    Guid CategoryId,
    string CategoryName,
    double Confidence,
    CategorizationSource Source,
    string Explanation,
    bool IsPrimary);

/// <summary>Stages of the categorization pipeline.</summary>
public enum CategorizationStage
{
    /// <summary>The transaction as it came in.</summary>
    Input,

    /// <summary>Rules.</summary>
    Rules,

    /// <summary>Payee default category.</summary>
    PayeeDefault,

    /// <summary>Learner.</summary>
    Learner,
}

/// <summary>What happened at one stage.</summary>
public enum CategorizationStepOutcome
{
    /// <summary>This stage decided the category.</summary>
    Decided,

    /// <summary>This stage ran and did not decide.</summary>
    NoDecision,

    /// <summary>This stage did not run because an earlier one decided, or it had nothing to work with.</summary>
    Skipped,
}

/// <summary>One stage of the trace.</summary>
/// <param name="Stage">Stage.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Detail">English detail, e.g. "Rule 'Groceries' set the category to Groceries."</param>
public sealed record CategorizationStep(CategorizationStage Stage, CategorizationStepOutcome Outcome, string Detail);

/// <summary>
/// "How was this categorized?" for the review screen (principle 7): each stage in order, the full
/// rule trace, the learner's prediction when it ran, and a one-line summary.
/// </summary>
/// <param name="Steps">Stages in pipeline order.</param>
/// <param name="Rules">The rule engine's trace.</param>
/// <param name="Learner">The learner's prediction, when it ran.</param>
/// <param name="Summary">One English sentence.</param>
public sealed record CategorizationTrace(
    IReadOnlyList<CategorizationStep> Steps,
    RuleTrace Rules,
    LearnerPrediction? Learner,
    string Summary);

/// <summary>The outcome of <see cref="ICategorizationEngine.Categorize(TransactionSnapshot, CompiledRuleSet, LearnerModel?, PayeeDefaultCategory?, int)"/>.</summary>
public sealed record CategorizationResult
{
    /// <summary>The rule run: mutation set (payee, memo, tags, approval, flag, splits, transfer, category) and its trace.</summary>
    public required RuleApplication Rules { get; init; }

    /// <summary>What decided the category.</summary>
    public required CategorizationSource DecidedBy { get; init; }

    /// <summary>
    /// The category to write when <see cref="DecidedBy"/> is <see cref="CategorizationSource.PayeeDefault"/>
    /// or <see cref="CategorizationSource.Learner"/>; for a rule it is already in <see cref="RuleApplication.Result"/>
    /// (null when the rule split the transaction or made it a transfer).
    /// </summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Suggestions for the review queue, best first, distinct categories.</summary>
    public IReadOnlyList<CategorizationSuggestion> Suggestions { get; init; } = [];

    /// <summary>The trace the review screen shows.</summary>
    public required CategorizationTrace Trace { get; init; }

    /// <summary>The transaction after rules, with <see cref="CategoryId"/> applied when a later stage decided it.</summary>
    public TransactionSnapshot Result => DecidedBy is CategorizationSource.PayeeDefault or CategorizationSource.Learner
        ? Rules.Result with { CategoryId = CategoryId }
        : Rules.Result;
}
