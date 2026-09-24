using System.Collections.Immutable;
using Keel.Domain.Entities;

namespace Keel.Domain.Categorization;

/// <summary>
/// The local, explainable, deterministic categorization learner (F-TXN-5, ADR 0021): a naive
/// Bayes classifier over approved history with Laplace smoothing and an exact-payee prior.
/// </summary>
public static class CategoryLearner
{
    /// <summary>No category is ever suggested below this confidence (F-TXN-5).</summary>
    public const double MinimumConfidence = 0.60;

    /// <summary>Approved examples a payee (or a payee word) needs before it can drive a suggestion.</summary>
    public const int MinimumPayeeExamples = 3;

    /// <summary>
    /// Trains a model. Deterministic: the same examples in the same order give the same model and
    /// the same JSON; the order only matters for category names given by several examples (the
    /// last one wins).
    /// </summary>
    /// <param name="examples">Approved, categorized history (see <see cref="LabeledExample"/>).</param>
    /// <param name="options">Tuning; <see cref="LearnerOptions.Default"/> when null.</param>
    /// <param name="categories">Category names and restrictions (hidden and system categories).</param>
    public static LearnerModel Train(IEnumerable<LabeledExample> examples, LearnerOptions? options = null, IEnumerable<LearnerCategory>? categories = null)
    {
        ArgumentNullException.ThrowIfNull(examples);
        options ??= LearnerOptions.Default;
        options.Validate();

        var classCounts = new Dictionary<Guid, int>();
        var tokenTotals = new Dictionary<Guid, int>();
        var payees = new CountTableBuilder<string>(StringComparer.Ordinal);
        var tokens = new CountTableBuilder<string>(StringComparer.Ordinal);
        var amounts = new CountTableBuilder<int>();
        var accounts = new CountTableBuilder<Guid>();
        var weekdays = new CountTableBuilder<int>();
        var directions = new CountTableBuilder<int>();
        var catalog = BuildCatalog(categories ?? []).ToBuilder();
        var normalizeCache = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var example in examples)
        {
            CheckExample(example);
            var c = example.CategoryId;
            var f = LearnerFeatures.Extract(example.Transaction, normalizeCache);
            classCounts[c] = classCounts.GetValueOrDefault(c) + 1;
            if (f.Payee.Length > 0)
            {
                payees.Add(f.Payee, c);
            }

            foreach (var token in f.Tokens)
            {
                tokens.Add(token, c);
            }

            if (f.Tokens.Length > 0)
            {
                tokenTotals[c] = tokenTotals.GetValueOrDefault(c) + f.Tokens.Length;
            }

            amounts.Add(f.AmountBucket, c);
            accounts.Add(f.AccountId, c);
            weekdays.Add(f.Weekday, c);
            directions.Add(f.Direction, c);
            if (!string.IsNullOrWhiteSpace(example.CategoryName))
            {
                catalog[c] = new LearnerCategory(c, example.CategoryName, catalog.TryGetValue(c, out var known) && known.IsRestricted);
            }
        }

        return new LearnerModel(
            options,
            classCounts.ToImmutableDictionary(),
            payees.Build(),
            tokens.Build(),
            tokenTotals.ToImmutableDictionary(),
            amounts.Build(),
            accounts.Build(),
            weekdays.Build(),
            directions.Build(),
            catalog.ToImmutable());
    }

    /// <summary>An empty model (no history): every prediction is <see cref="LearnerOutcome.NoHistory"/>.</summary>
    public static LearnerModel Empty(LearnerOptions? options = null, IEnumerable<LearnerCategory>? categories = null) =>
        Train([], options, categories);

    internal static ImmutableDictionary<Guid, LearnerCategory> BuildCatalog(IEnumerable<LearnerCategory> categories)
    {
        var builder = ImmutableDictionary.CreateBuilder<Guid, LearnerCategory>();
        foreach (var category in categories)
        {
            builder[category.Id] = category;
        }

        if (!builder.ContainsKey(SystemIds.ReadyToAssignCategory))
        {
            builder[SystemIds.ReadyToAssignCategory] = new LearnerCategory(SystemIds.ReadyToAssignCategory, "Ready to Assign", IsRestricted: true);
        }

        return builder.ToImmutable();
    }

    internal static void CheckExample(LabeledExample example)
    {
        ArgumentNullException.ThrowIfNull(example);
        ArgumentNullException.ThrowIfNull(example.Transaction);
        if (example.CategoryId == Guid.Empty)
        {
            throw new ArgumentException("A labeled example needs a category.", nameof(example));
        }
    }
}
