using System.Globalization;
using Keel.Domain.Categorization;
using Keel.Domain.Rules;

namespace Keel.Application.Categorization;

/// <summary>The stateless implementation of <see cref="ICategorizationEngine"/> (ADR 0022).</summary>
public sealed class CategorizationEngine : ICategorizationEngine
{
    /// <summary>Confidence given to a payee's default category (F-TXN-9).</summary>
    public const double PayeeDefaultConfidence = 0.95;

    /// <inheritdoc />
    public CategorizationResult Categorize(TransactionSnapshot snapshot, IEnumerable<RuleDefinition> rules, LearnerModel? model, PayeeDefaultCategory? payeeDefault, int topN = 5) =>
        Categorize(snapshot, RuleEngine.Compile(rules), model, payeeDefault, topN);

    /// <inheritdoc />
    public CategorizationResult Categorize(TransactionSnapshot snapshot, CompiledRuleSet rules, LearnerModel? model, PayeeDefaultCategory? payeeDefault, int topN = 5)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rules);
        var steps = new List<CategorizationStep>();
        var application = rules.Apply(snapshot);
        var after = application.Result;
        var mutations = application.Mutations;

        // 1. Rules.
        if (mutations.DecidesCategory)
        {
            var ruleName = RuleThatDecided(application);
            var detail = RuleDetail(after, ruleName, model);
            steps.Add(new(CategorizationStage.Rules, CategorizationStepOutcome.Decided, detail));
            steps.Add(new(CategorizationStage.PayeeDefault, CategorizationStepOutcome.Skipped, "Not needed: a rule decided."));
            steps.Add(new(CategorizationStage.Learner, CategorizationStepOutcome.Skipped, "Not consulted: a rule decided."));
            var suggestions = after.CategoryId is { } ruleCategory
                ? new[] { new CategorizationSuggestion(ruleCategory, CategoryName(ruleCategory, model, payeeDefault), 1.0, CategorizationSource.Rule, $"Set by rule '{ruleName}'", true) }
                : [];
            return Result(application, CategorizationSource.Rule, null, suggestions, steps, null, detail);
        }

        steps.Add(new(
            CategorizationStage.Rules,
            CategorizationStepOutcome.NoDecision,
            application.Trace.Matched.Count == 0
                ? (application.Trace.Evaluations.Count == 0 ? "No rules." : "No rule matched.")
                : $"Matched {string.Join(", ", application.Trace.Matched.Select(m => "'" + m.RuleName + "'"))}; none set a category."));

        // 0. Already categorized on the way in (manual entry, re-run): keep it.
        if (after.CategoryId is not null || after.IsSplit || after.TransferAccountId is not null)
        {
            const string kept = "Already categorized; kept as is.";
            steps.Insert(0, new(CategorizationStage.Input, CategorizationStepOutcome.Decided, kept));
            steps.Add(new(CategorizationStage.PayeeDefault, CategorizationStepOutcome.Skipped, "Not needed: already categorized."));
            steps.Add(new(CategorizationStage.Learner, CategorizationStepOutcome.Skipped, "Not needed: already categorized."));
            return Result(application, CategorizationSource.Existing, after.CategoryId, [], steps, null, kept);
        }

        var prediction = model?.Predict(after, Math.Max(topN, 1));

        // 2. Payee default category.
        if (payeeDefault is not null)
        {
            var payee = string.IsNullOrWhiteSpace(after.Payee) ? after.PayeeRaw : after.Payee;
            var explanation = $"Suggested because {payee.Trim()}'s default category is {payeeDefault.CategoryName}";
            steps.Add(new(CategorizationStage.PayeeDefault, CategorizationStepOutcome.Decided, explanation + "."));
            steps.Add(new(
                CategorizationStage.Learner,
                prediction is null ? CategorizationStepOutcome.Skipped : CategorizationStepOutcome.NoDecision,
                prediction is null ? "No learner model." : "Consulted for alternatives only: " + prediction.Reason));
            var suggestions = new List<CategorizationSuggestion>
            {
                new(payeeDefault.CategoryId, payeeDefault.CategoryName, PayeeDefaultConfidence, CategorizationSource.PayeeDefault, explanation, true),
            };
            AddLearnerAlternatives(suggestions, prediction, topN);
            var summary = $"{explanation} ({Percent(PayeeDefaultConfidence)}).";
            return Result(application, CategorizationSource.PayeeDefault, payeeDefault.CategoryId, Cap(suggestions, topN), steps, prediction, summary);
        }

        steps.Add(new(CategorizationStage.PayeeDefault, CategorizationStepOutcome.Skipped, "The payee has no default category."));

        // 3. Learner.
        if (prediction is null)
        {
            steps.Add(new(CategorizationStage.Learner, CategorizationStepOutcome.Skipped, "No learner model yet."));
            return Result(application, CategorizationSource.None, null, [], steps, null, "Left uncategorized: no rule, payee default or learning history applies.");
        }

        if (prediction.Outcome != LearnerOutcome.Suggested || topN <= 0)
        {
            steps.Add(new(CategorizationStage.Learner, CategorizationStepOutcome.NoDecision, prediction.Reason));
            return Result(application, CategorizationSource.None, null, [], steps, prediction, "Left uncategorized. " + prediction.Reason);
        }

        var learnerSuggestions = prediction.Suggestions
            .Select(s => new CategorizationSuggestion(s.CategoryId, s.CategoryName, s.Confidence, CategorizationSource.Learner, s.Explanation, s.IsPrimary))
            .ToList();
        steps.Add(new(CategorizationStage.Learner, CategorizationStepOutcome.Decided, prediction.Reason));
        return Result(application, CategorizationSource.Learner, learnerSuggestions[0].CategoryId, Cap(learnerSuggestions, topN), steps, prediction, prediction.Reason);
    }

    private static CategorizationResult Result(
        RuleApplication application,
        CategorizationSource source,
        Guid? category,
        IReadOnlyList<CategorizationSuggestion> suggestions,
        List<CategorizationStep> steps,
        LearnerPrediction? prediction,
        string summary) => new()
        {
            Rules = application,
            DecidedBy = source,
            CategoryId = category,
            Suggestions = suggestions,
            Trace = new CategorizationTrace(steps, application.Trace, prediction, summary),
        };

    private static void AddLearnerAlternatives(List<CategorizationSuggestion> suggestions, LearnerPrediction? prediction, int topN)
    {
        if (prediction is null || prediction.Outcome != LearnerOutcome.Suggested)
        {
            return;
        }

        foreach (var s in prediction.Suggestions)
        {
            if (suggestions.Count >= topN)
            {
                break;
            }

            if (suggestions.All(existing => existing.CategoryId != s.CategoryId))
            {
                suggestions.Add(new CategorizationSuggestion(s.CategoryId, s.CategoryName, s.Confidence, CategorizationSource.Learner, s.Explanation, false));
            }
        }
    }

    private static IReadOnlyList<CategorizationSuggestion> Cap(List<CategorizationSuggestion> suggestions, int topN) =>
        topN <= 0 ? [] : suggestions.Take(topN).ToArray();

    private static string RuleThatDecided(RuleApplication application)
    {
        var sets = application.Mutations.SetBy;
        Guid? id = null;
        foreach (var field in new[] { RuleChanges.Category, RuleChanges.Splits, RuleChanges.TransferAccount })
        {
            if (sets.TryGetValue(field, out var ruleId))
            {
                id = ruleId;
                break;
            }
        }

        return application.Trace.Evaluations.LastOrDefault(e => e.RuleId == id)?.RuleName
            ?? application.Trace.Matched.LastOrDefault()?.RuleName
            ?? string.Empty;
    }

    private static string RuleDetail(TransactionSnapshot after, string ruleName, LearnerModel? model)
    {
        if (after.IsSplit)
        {
            return $"Split by rule '{ruleName}' into {after.Splits.Count.ToString(CultureInfo.InvariantCulture)} parts.";
        }

        if (after.TransferAccountId is not null && after.CategoryId is null)
        {
            return $"Made a transfer by rule '{ruleName}'.";
        }

        return $"Category set to {CategoryName(after.CategoryId!.Value, model, null)} by rule '{ruleName}'.";
    }

    private static string CategoryName(Guid id, LearnerModel? model, PayeeDefaultCategory? payeeDefault)
    {
        if (payeeDefault?.CategoryId == id)
        {
            return payeeDefault.CategoryName;
        }

        return model?.Categories.TryGetValue(id, out var info) == true && !string.IsNullOrWhiteSpace(info.Name)
            ? info.Name
            : "category #" + id.ToString("N", CultureInfo.InvariantCulture)[..8];
    }

    private static string Percent(double value) => value.ToString("0%", CultureInfo.InvariantCulture);
}
