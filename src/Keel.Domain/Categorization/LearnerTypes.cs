using Keel.Domain.Rules;

namespace Keel.Domain.Categorization;

/// <summary>
/// One approved, categorized transaction the learner learns from (F-TXN-5). Callers pass
/// approved, non-split, non-transfer history; <paramref name="CategoryName"/>, when given, is
/// remembered for explanations.
/// </summary>
/// <param name="Transaction">The transaction as approved.</param>
/// <param name="CategoryId">Its category.</param>
/// <param name="CategoryName">The category's display name.</param>
public sealed record LabeledExample(TransactionSnapshot Transaction, Guid CategoryId, string? CategoryName = null);

/// <summary>
/// What the learner needs to know about a category: its name for explanations, and whether it
/// is restricted (hidden, or a system Inflow / Credit Card Payment category), which it suggests
/// only when the evidence is exclusively in restricted categories.
/// </summary>
/// <param name="Id">Category id.</param>
/// <param name="Name">Display name.</param>
/// <param name="IsRestricted">Hidden or system category.</param>
public sealed record LearnerCategory(Guid Id, string Name, bool IsRestricted);

/// <summary>Tuning knobs of the learner. Stored with the model so a cached model predicts the same way.</summary>
public sealed record LearnerOptions
{
    /// <summary>The defaults (ADR 0021).</summary>
    public static LearnerOptions Default { get; } = new();

    /// <summary>Laplace (add-alpha) smoothing for every count.</summary>
    public double Smoothing { get; init; } = 1.0;

    /// <summary>
    /// Pseudo-count of the global category prior mixed into a payee's own category distribution
    /// (the exact-payee prior). Small values let the payee's history dominate.
    /// </summary>
    public double PayeePriorStrength { get; init; } = 1.0;

    /// <summary>Exponent applied to the amount, account, weekday and direction likelihoods (tempering for correlated features).</summary>
    public double ContextWeight { get; init; } = 0.5;

    /// <summary>Exponent applied to each payee-token likelihood.</summary>
    public double TokenWeight { get; init; } = 1.0;

    /// <summary>Alternatives after the primary suggestion are listed only down to this confidence.</summary>
    public double MinimumAlternativeConfidence { get; init; } = 0.05;

    internal void Validate()
    {
        if (!(Smoothing > 0) || !(PayeePriorStrength > 0) || !(ContextWeight >= 0) || !(TokenWeight >= 0)
            || !(MinimumAlternativeConfidence >= 0 && MinimumAlternativeConfidence <= 1))
        {
            throw new ArgumentException("Learner options are out of range: smoothing and payee prior strength must be positive, weights non-negative, alternative confidence in [0, 1].");
        }
    }
}

/// <summary>What a suggestion is based on.</summary>
public enum SuggestionBasis
{
    /// <summary>The exact normalized payee has at least three approved examples; its history dominates.</summary>
    ExactPayee,

    /// <summary>Words of the payee shared with other approved transactions.</summary>
    PayeeTokens,
}

/// <summary>A payee word and how often it went to the suggested category.</summary>
/// <param name="Token">The word (normalized, upper case).</param>
/// <param name="CategoryExamples">Approved examples containing the word in the suggested category.</param>
/// <param name="TotalExamples">Approved examples containing the word.</param>
public sealed record TokenEvidence(string Token, int CategoryExamples, int TotalExamples);

/// <summary>A category suggestion from the learner.</summary>
public sealed record CategorySuggestion
{
    /// <summary>Suggested category.</summary>
    public required Guid CategoryId { get; init; }

    /// <summary>Category name (from the model's catalog), for display.</summary>
    public required string CategoryName { get; init; }

    /// <summary>Posterior probability in [0, 1].</summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// True for the first suggestion, the only one that may be written to the transaction; it is
    /// always at least <see cref="CategoryLearner.MinimumConfidence"/>. The others are alternatives
    /// for the review picker.
    /// </summary>
    public required bool IsPrimary { get; init; }

    /// <summary>What drove the suggestion.</summary>
    public required SuggestionBasis Basis { get; init; }

    /// <summary>An English sentence such as "Suggested because 12 of 13 past 'TRADER JOES' transactions were Groceries".</summary>
    public required string Explanation { get; init; }

    /// <summary>The normalized payee the learner looked at.</summary>
    public required string NormalizedPayee { get; init; }

    /// <summary>For <see cref="SuggestionBasis.ExactPayee"/>: approved examples of the payee in this category.</summary>
    public int PayeeCategoryExamples { get; init; }

    /// <summary>For <see cref="SuggestionBasis.ExactPayee"/>: approved examples of the payee.</summary>
    public int PayeeExamples { get; init; }

    /// <summary>For <see cref="SuggestionBasis.PayeeTokens"/>: the words that drove it.</summary>
    public IReadOnlyList<TokenEvidence> Tokens { get; init; } = [];

    /// <summary>Context features (amount, account, day of week, direction) that favour this category over the payee's usual one.</summary>
    public IReadOnlyList<string> SupportingContext { get; init; } = [];
}

/// <summary>Why the learner did or did not suggest.</summary>
public enum LearnerOutcome
{
    /// <summary>A primary suggestion at or above the confidence floor.</summary>
    Suggested,

    /// <summary>The model has no examples.</summary>
    NoHistory,

    /// <summary>The payee has fewer than three examples and none of its words has three.</summary>
    NotEnoughPayeeHistory,

    /// <summary>The best allowed category is below the confidence floor.</summary>
    BelowThreshold,
}

/// <summary>A category and its posterior, for diagnostics.</summary>
/// <param name="CategoryId">Category.</param>
/// <param name="CategoryName">Name.</param>
/// <param name="Confidence">Posterior probability.</param>
/// <param name="Excluded">Restricted and not allowed for this transaction.</param>
public sealed record LearnerCandidate(Guid CategoryId, string CategoryName, double Confidence, bool Excluded);

/// <summary>The full learner output: suggestions plus why (for the categorization trace).</summary>
public sealed record LearnerPrediction
{
    /// <summary>What happened.</summary>
    public required LearnerOutcome Outcome { get; init; }

    /// <summary>Suggestions; empty unless <see cref="Outcome"/> is <see cref="LearnerOutcome.Suggested"/>.</summary>
    public IReadOnlyList<CategorySuggestion> Suggestions { get; init; } = [];

    /// <summary>The normalized payee.</summary>
    public string NormalizedPayee { get; init; } = string.Empty;

    /// <summary>Approved examples of the exact normalized payee.</summary>
    public int PayeeExamples { get; init; }

    /// <summary>Basis used, when a payee feature qualified.</summary>
    public SuggestionBasis? Basis { get; init; }

    /// <summary>The top candidates by posterior (at most five), including excluded ones.</summary>
    public IReadOnlyList<LearnerCandidate> TopCandidates { get; init; } = [];

    /// <summary>An English sentence explaining the outcome.</summary>
    public string Reason { get; init; } = string.Empty;
}
