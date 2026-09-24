using Keel.Application.Categorization;

namespace Keel.Infrastructure.Categorization;

/// <summary>
/// The import pipeline's categorization step (F-TXN-1 step 4): rules, then the payee default or
/// the learner (at least 60%) for rows still uncategorized, over freshly inserted transactions.
/// It has the method shape of the import pipeline's <c>IImportCategorizationHook</c> seam, which is
/// not on <c>main</c> yet; wiring it is one DI line (ADR 0025).
/// </summary>
public sealed class ImportCategorizationAdapter(ICategorizationService categorization)
{
    /// <summary>Categorizes the inserted transactions in one undoable action.</summary>
    public Task ApplyAsync(IReadOnlyList<Guid> transactionIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transactionIds);
        return transactionIds.Count == 0 ? Task.CompletedTask : categorization.CategorizeAsync(transactionIds, ct);
    }
}
