using System.Collections.Immutable;
using System.Globalization;
using Keel.Domain.Entities;
using Keel.Domain.Rules;

namespace Keel.Domain.Categorization;

/// <summary>
/// A trained categorization model (F-TXN-5): integer counts only, immutable, JSON-serializable
/// (<see cref="LearnerModelJson"/>). Built by <see cref="CategoryLearner.Train"/> and updated
/// incrementally with <see cref="WithExample"/> and <see cref="WithoutExample"/>; the result is the
/// same model a full retrain would produce. Thread-safe.
/// </summary>
public sealed class LearnerModel
{
    private static readonly string[] ContextNames = ["amount", "account", "day of week", "direction"];

    internal LearnerModel(
        LearnerOptions options,
        ImmutableDictionary<Guid, int> classCounts,
        CountTable<string> payees,
        CountTable<string> tokens,
        ImmutableDictionary<Guid, int> tokenTotals,
        CountTable<int> amounts,
        CountTable<Guid> accounts,
        CountTable<int> weekdays,
        CountTable<int> directions,
        ImmutableDictionary<Guid, LearnerCategory> catalog)
    {
        Options = options;
        ClassCounts = classCounts;
        Payees = payees;
        Tokens = tokens;
        TokenTotals = tokenTotals;
        Amounts = amounts;
        Accounts = accounts;
        Weekdays = weekdays;
        Directions = directions;
        Catalog = catalog;
        Classes = [.. classCounts.Keys.Order()];
        ExampleCount = classCounts.Values.Sum();
    }

    /// <summary>Options the model predicts with.</summary>
    public LearnerOptions Options { get; }

    /// <summary>Number of examples learned.</summary>
    public int ExampleCount { get; }

    /// <summary>Categories with at least one example, in id order.</summary>
    public ImmutableArray<Guid> Classes { get; }

    /// <summary>Number of distinct normalized payees seen.</summary>
    public int PayeeCount => Payees.Count;

    /// <summary>Known categories (names and restrictions).</summary>
    public IReadOnlyDictionary<Guid, LearnerCategory> Categories => Catalog;

    internal ImmutableDictionary<Guid, int> ClassCounts { get; }

    internal CountTable<string> Payees { get; }

    internal CountTable<string> Tokens { get; }

    internal ImmutableDictionary<Guid, int> TokenTotals { get; }

    internal CountTable<int> Amounts { get; }

    internal CountTable<Guid> Accounts { get; }

    internal CountTable<int> Weekdays { get; }

    internal CountTable<int> Directions { get; }

    internal ImmutableDictionary<Guid, LearnerCategory> Catalog { get; }

    /// <summary>Approved examples of a payee, by category (the payee is normalized first).</summary>
    public IReadOnlyDictionary<Guid, int> PayeeHistory(string payee) =>
        Payees.Row(Import.PayeeNormalizer.Normalize(payee)) ?? ImmutableDictionary<Guid, int>.Empty;

    /// <summary>The same model after learning one more example (an approval).</summary>
    public LearnerModel WithExample(LabeledExample example) => Update(example, +1);

    /// <summary>
    /// The same model without an example it learned earlier (an approval undone, or a category
    /// changed: remove the old example and add the new one). Throws when the example was not learned.
    /// </summary>
    public LearnerModel WithoutExample(LabeledExample example) => Update(example, -1);

    /// <summary>Replaces category names and restrictions (after a rename, hide or unhide). Counts are unchanged.</summary>
    public LearnerModel WithCategories(IEnumerable<LearnerCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        return new LearnerModel(Options, ClassCounts, Payees, Tokens, TokenTotals, Amounts, Accounts, Weekdays, Directions, CategoryLearner.BuildCatalog(categories));
    }

    /// <summary>Serializes the model to deterministic JSON (same model, same bytes).</summary>
    public string ToJson() => LearnerModelJson.Serialize(this);

    /// <summary>Reads a model written by <see cref="ToJson"/>. Throws <see cref="LearnerModelFormatException"/> when it cannot.</summary>
    public static LearnerModel FromJson(string json) => LearnerModelJson.Deserialize(json);

    /// <summary>
    /// Up to <paramref name="topN"/> category suggestions for a transaction, best first. Empty
    /// unless the best allowed category reaches <see cref="CategoryLearner.MinimumConfidence"/>.
    /// Only the first (<see cref="CategorySuggestion.IsPrimary"/>) may be written to the
    /// transaction; the rest are alternatives for the review picker.
    /// </summary>
    public IReadOnlyList<CategorySuggestion> Suggest(TransactionSnapshot snapshot, int topN = 5) =>
        Predict(snapshot, topN).Suggestions;

    /// <summary>Like <see cref="Suggest"/>, plus the outcome, top candidates and a reason, for the categorization trace.</summary>
    public LearnerPrediction Predict(TransactionSnapshot snapshot, int topN = 5)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (ExampleCount == 0)
        {
            return new LearnerPrediction { Outcome = LearnerOutcome.NoHistory, Reason = "No approved transactions to learn from yet." };
        }

        var features = LearnerFeatures.Extract(snapshot);
        var payeeRow = features.Payee.Length > 0 ? Payees.Row(features.Payee) : null;
        var payeeExamples = payeeRow?.Values.Sum() ?? 0;
        var usePayee = payeeExamples >= CategoryLearner.MinimumPayeeExamples;
        var qualifying = new List<(string Token, ImmutableDictionary<Guid, int> Row, int Support)>();
        if (!usePayee)
        {
            foreach (var token in features.Tokens)
            {
                if (Tokens.Row(token) is { } row && row.Values.Sum() is var support && support >= CategoryLearner.MinimumPayeeExamples)
                {
                    qualifying.Add((token, row, support));
                }
            }
        }

        if (!usePayee && qualifying.Count == 0)
        {
            return new LearnerPrediction
            {
                Outcome = LearnerOutcome.NotEnoughPayeeHistory,
                NormalizedPayee = features.Payee,
                PayeeExamples = payeeExamples,
                Reason = payeeExamples == 0
                    ? $"Not suggested: no approved transactions from '{features.Payee}' or with its words yet."
                    : $"Not suggested: only {Count(payeeExamples, "approved transaction")} from '{features.Payee}' (at least {CategoryLearner.MinimumPayeeExamples} needed).",
            };
        }

        var basis = usePayee ? SuggestionBasis.ExactPayee : SuggestionBasis.PayeeTokens;
        var posterior = Posterior(features, usePayee ? payeeRow : null, payeeExamples, qualifying);

        // Evidence decides whether restricted (hidden / system) categories are allowed.
        var evidence = usePayee
            ? payeeRow!.Keys
            : qualifying.SelectMany(q => q.Row.Keys).Distinct();
        var allowRestricted = evidence.All(IsRestricted);
        var ranked = Enumerable.Range(0, Classes.Length)
            .OrderByDescending(i => posterior[i])
            .ThenBy(i => Classes[i])
            .ToArray();
        var top = ranked.Take(5)
            .Select(i => new LearnerCandidate(Classes[i], NameOf(Classes[i]), posterior[i], IsRestricted(Classes[i]) && !allowRestricted))
            .ToArray();
        // With an exact-payee history, only categories the payee actually had are eligible.
        var allowed = ranked
            .Where(i => allowRestricted || !IsRestricted(Classes[i]))
            .Where(i => !usePayee || payeeRow!.ContainsKey(Classes[i]))
            .ToArray();

        if (allowed.Length == 0 || posterior[allowed[0]] < CategoryLearner.MinimumConfidence)
        {
            var best = allowed.Length > 0 ? allowed[0] : -1;
            return new LearnerPrediction
            {
                Outcome = LearnerOutcome.BelowThreshold,
                NormalizedPayee = features.Payee,
                PayeeExamples = payeeExamples,
                Basis = basis,
                TopCandidates = top,
                Reason = best < 0
                    ? "Not suggested: the history only points to hidden or system categories."
                    : $"Not suggested: the best guess, {NameOf(Classes[best])} at {Percent(posterior[best])}, is below {Percent(CategoryLearner.MinimumConfidence)}.",
            };
        }

        var majority = usePayee ? MostFrequent(payeeRow!) : (Guid?)null;
        var suggestions = new List<CategorySuggestion>();
        foreach (var i in allowed)
        {
            if (suggestions.Count >= Math.Max(topN, 1) || (suggestions.Count > 0 && posterior[i] < Options.MinimumAlternativeConfidence))
            {
                break;
            }

            var outranksUsual = majority is { } usual && usual != Classes[i] && posterior[i] > posterior[Classes.IndexOf(usual)];
            suggestions.Add(Describe(features, Classes[i], posterior[i], suggestions.Count == 0, basis, payeeRow, payeeExamples, qualifying, outranksUsual ? majority : null));
        }

        return new LearnerPrediction
        {
            Outcome = LearnerOutcome.Suggested,
            Suggestions = topN <= 0 ? [] : suggestions,
            NormalizedPayee = features.Payee,
            PayeeExamples = payeeExamples,
            Basis = basis,
            TopCandidates = top,
            Reason = suggestions[0].Explanation + $" ({Percent(suggestions[0].Confidence)} confident).",
        };
    }

    private double[] Posterior(
        LearnerFeatures f,
        ImmutableDictionary<Guid, int>? payeeRow,
        int payeeExamples,
        List<(string Token, ImmutableDictionary<Guid, int> Row, int Support)> tokens)
    {
        var alpha = Options.Smoothing;
        var beta = Options.PayeePriorStrength;
        var k = Classes.Length;
        var priorDenominator = ExampleCount + (alpha * k);
        var tokenVocabulary = Tokens.Count + 1;
        var context = ContextRows(f);
        var scores = new double[k];
        for (var i = 0; i < k; i++)
        {
            var c = Classes[i];
            var n = ClassCounts[c];
            var prior = (n + alpha) / priorDenominator;
            double score;
            if (payeeRow is not null)
            {
                score = Math.Log((payeeRow.GetValueOrDefault(c) + (beta * prior)) / (payeeExamples + beta));
            }
            else
            {
                score = Math.Log(prior);
                var tokenDenominator = TokenTotals.GetValueOrDefault(c) + (alpha * tokenVocabulary);
                foreach (var (_, row, _) in tokens)
                {
                    score += Options.TokenWeight * Math.Log((row.GetValueOrDefault(c) + alpha) / tokenDenominator);
                }
            }

            foreach (var (row, vocabulary) in context)
            {
                score += Options.ContextWeight * Math.Log(((row?.GetValueOrDefault(c) ?? 0) + alpha) / (n + (alpha * vocabulary)));
            }

            scores[i] = score;
        }

        var max = scores.Max();
        var sum = 0.0;
        for (var i = 0; i < k; i++)
        {
            scores[i] = Math.Exp(scores[i] - max);
            sum += scores[i];
        }

        for (var i = 0; i < k; i++)
        {
            scores[i] /= sum;
        }

        return scores;
    }

    private (ImmutableDictionary<Guid, int>? Row, int Vocabulary)[] ContextRows(LearnerFeatures f) =>
    [
        (Amounts.Row(f.AmountBucket), Amounts.Count + 1),
        (Accounts.Row(f.AccountId), Accounts.Count + 1),
        (Weekdays.Row(f.Weekday), Weekdays.Count + 1),
        (Directions.Row(f.Direction), Directions.Count + 1),
    ];

    private CategorySuggestion Describe(
        LearnerFeatures f,
        Guid category,
        double confidence,
        bool primary,
        SuggestionBasis basis,
        ImmutableDictionary<Guid, int>? payeeRow,
        int payeeExamples,
        List<(string Token, ImmutableDictionary<Guid, int> Row, int Support)> tokens,
        Guid? outranked)
    {
        var name = NameOf(category);
        string explanation;
        var supporting = Array.Empty<string>();
        var tokenEvidence = Array.Empty<TokenEvidence>();
        var inCategory = 0;
        if (basis == SuggestionBasis.ExactPayee)
        {
            inCategory = payeeRow!.GetValueOrDefault(category);
            if (outranked is { } usual)
            {
                supporting = SupportingContext(f, category, usual);
            }

            explanation = $"Suggested because {inCategory} of {payeeExamples} past '{f.Payee}' transactions were {name}";
            if (supporting.Length > 0)
            {
                explanation += $"; its {JoinAnd(supporting)} fit {name} better than {NameOf(outranked!.Value)}";
            }
        }
        else
        {
            tokenEvidence = tokens
                .Select(t => new TokenEvidence(t.Token, t.Row.GetValueOrDefault(category), t.Support))
                .Where(t => t.CategoryExamples > 0)
                .OrderByDescending(t => (double)t.CategoryExamples / t.TotalExamples)
                .ThenByDescending(t => t.TotalExamples)
                .ThenBy(t => t.Token, StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            explanation = tokenEvidence.Length switch
            {
                0 => $"Suggested because transactions with a similar amount, account and day were {name}",
                1 => $"Suggested because {tokenEvidence[0].CategoryExamples} of {tokenEvidence[0].TotalExamples} past transactions with '{tokenEvidence[0].Token}' in the payee were {name}",
                _ => $"Suggested because {tokenEvidence[0].CategoryExamples} of {tokenEvidence[0].TotalExamples} past transactions with '{tokenEvidence[0].Token}' "
                    + $"and {tokenEvidence[1].CategoryExamples} of {tokenEvidence[1].TotalExamples} with '{tokenEvidence[1].Token}' in the payee were {name}",
            };
        }

        return new CategorySuggestion
        {
            CategoryId = category,
            CategoryName = name,
            Confidence = confidence,
            IsPrimary = primary,
            Basis = basis,
            Explanation = explanation,
            NormalizedPayee = f.Payee,
            PayeeCategoryExamples = inCategory,
            PayeeExamples = basis == SuggestionBasis.ExactPayee ? payeeExamples : 0,
            Tokens = tokenEvidence,
            SupportingContext = supporting,
        };
    }

    private string[] SupportingContext(LearnerFeatures f, Guid category, Guid usual)
    {
        var alpha = Options.Smoothing;
        var rows = ContextRows(f);
        var result = new List<string>();
        for (var g = 0; g < rows.Length; g++)
        {
            var (row, vocabulary) = rows[g];
            double Likelihood(Guid c) => ((row?.GetValueOrDefault(c) ?? 0) + alpha) / (ClassCounts[c] + (alpha * vocabulary));
            if (Likelihood(category) > Likelihood(usual))
            {
                result.Add(ContextNames[g]);
            }
        }

        return [.. result];
    }

    private LearnerModel Update(LabeledExample example, int delta)
    {
        ArgumentNullException.ThrowIfNull(example);
        CategoryLearner.CheckExample(example);
        var f = LearnerFeatures.Extract(example.Transaction);
        var c = example.CategoryId;
        var classCount = ClassCounts.GetValueOrDefault(c) + delta;
        if (classCount < 0)
        {
            throw new InvalidOperationException("The example is not part of the model.");
        }

        var tokens = Tokens;
        foreach (var token in f.Tokens)
        {
            tokens = tokens.Add(token, c, delta);
        }

        var tokenTotal = TokenTotals.GetValueOrDefault(c) + (delta * f.Tokens.Length);
        var catalog = Catalog;
        if (delta > 0 && !string.IsNullOrWhiteSpace(example.CategoryName))
        {
            catalog = catalog.SetItem(c, new LearnerCategory(c, example.CategoryName, catalog.TryGetValue(c, out var known) && known.IsRestricted));
        }

        return new LearnerModel(
            Options,
            classCount == 0 ? ClassCounts.Remove(c) : ClassCounts.SetItem(c, classCount),
            f.Payee.Length > 0 ? Payees.Add(f.Payee, c, delta) : Payees,
            tokens,
            tokenTotal == 0 ? TokenTotals.Remove(c) : TokenTotals.SetItem(c, tokenTotal),
            Amounts.Add(f.AmountBucket, c, delta),
            Accounts.Add(f.AccountId, c, delta),
            Weekdays.Add(f.Weekday, c, delta),
            Directions.Add(f.Direction, c, delta),
            catalog);
    }

    private bool IsRestricted(Guid category) =>
        category == SystemIds.ReadyToAssignCategory || (Catalog.TryGetValue(category, out var info) && info.IsRestricted);

    private string NameOf(Guid category) =>
        Catalog.TryGetValue(category, out var info) && !string.IsNullOrWhiteSpace(info.Name)
            ? info.Name
            : category == SystemIds.ReadyToAssignCategory ? "Ready to Assign" : "category #" + category.ToString("N", CultureInfo.InvariantCulture)[..8];

    private static Guid MostFrequent(ImmutableDictionary<Guid, int> row) =>
        row.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;

    private static string Percent(double value) => value.ToString("0%", CultureInfo.InvariantCulture);

    private static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? string.Empty : "s");

    private static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => items[0] + " and " + items[1],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
