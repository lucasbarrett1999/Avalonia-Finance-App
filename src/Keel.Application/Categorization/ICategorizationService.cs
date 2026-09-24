using Keel.Domain.Rules;

namespace Keel.Application.Categorization;

/// <summary>
/// The database-backed categorization pipeline (F-TXN-1 step 4, F-TXN-5, F-TXN-6): runs
/// <see cref="ICategorizationEngine"/> with the stored rules, the cached learner model
/// (<see cref="ILearnerService"/>) and payee default categories. Rules write; the learner only
/// suggests, and its primary suggestion is written only by <see cref="CategorizeAsync"/> (import)
/// or by an explicit review decision, never below 60% confidence. Writes are audited, undoable
/// ledger actions.
/// </summary>
public interface ICategorizationService
{
    /// <summary>
    /// Suggestions for one transaction, as the review queue shows them: rule match first, then
    /// the payee default and learner suggestions with confidence and explanation. A transaction
    /// that already has a (single) category is scored as if it had none, so its suggestions still
    /// show; split and transfer transactions keep what they are. Null when the transaction does not exist.
    /// </summary>
    Task<TransactionCategorization?> SuggestAsync(Guid transactionId, CancellationToken ct);

    /// <summary>Like <see cref="SuggestAsync"/> for many transactions (rules compiled once).</summary>
    Task<IReadOnlyList<TransactionCategorization>> SuggestManyAsync(IReadOnlyCollection<Guid> transactionIds, CancellationToken ct);

    /// <summary>Applies the stored rules' mutations to the transactions (rules write; no learner). One undoable action.</summary>
    Task<CategorizationWriteResult> ApplyRulesAsync(IReadOnlyList<Guid> transactionIds, CancellationToken ct);

    /// <summary>
    /// The full import-time pipeline: rule mutations, then the payee default or the learner's
    /// primary suggestion (at least 60%) for transactions that are still uncategorized. One undoable action.
    /// </summary>
    Task<CategorizationWriteResult> CategorizeAsync(IReadOnlyList<Guid> transactionIds, CancellationToken ct);

    /// <summary>
    /// Records review decisions and approves the transactions in one undoable action: optional
    /// rule mutations, then the chosen category (unless the row is split or a transfer that takes
    /// no category), then approval. The learner learns from the approvals.
    /// </summary>
    Task<int> ApproveAsync(IReadOnlyList<ReviewDecision> decisions, CancellationToken ct);

    /// <summary>
    /// "Approve all with confidence ≥ x": a decision for every unapproved transaction whose
    /// primary suggestion reaches <paramref name="minimumConfidence"/> and does not contradict a
    /// category the transaction already has.
    /// </summary>
    Task<IReadOnlyList<ReviewDecision>> PlanBatchApprovalAsync(double minimumConfidence, CancellationToken ct);
}

/// <summary>A transaction and its categorization result.</summary>
/// <param name="TransactionId">Transaction.</param>
/// <param name="Snapshot">The transaction as stored.</param>
/// <param name="Result">Engine result (suggestions, trace, rule mutations).</param>
public sealed record TransactionCategorization(Guid TransactionId, TransactionSnapshot Snapshot, CategorizationResult Result)
{
    /// <summary>The primary suggestion, if any.</summary>
    public CategorizationSuggestion? Primary => Result.Suggestions.FirstOrDefault(s => s.IsPrimary);
}

/// <summary>A review decision.</summary>
/// <param name="TransactionId">Transaction to approve.</param>
/// <param name="CategoryId">Category to write, or null to keep the current one.</param>
/// <param name="ApplyRules">Also write the stored rules' mutations (when the chosen suggestion came from a rule).</param>
public sealed record ReviewDecision(Guid TransactionId, Guid? CategoryId, bool ApplyRules = false);

/// <summary>Outcome of a categorization write.</summary>
/// <param name="Examined">Transactions examined.</param>
/// <param name="Changed">Transactions changed.</param>
public sealed record CategorizationWriteResult(int Examined, int Changed);
