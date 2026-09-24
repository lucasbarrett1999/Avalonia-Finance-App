using Keel.Domain;

namespace Keel.Application.Import;

/// <summary>
/// The seam for F-TXN-1 steps 3 and 4 (payee rename rules, categorization rules, then the
/// learner). The import pipeline calls every registered hook, in registration order, for the rows
/// it is about to insert; M4's rules engine and learner register their own implementation next to
/// the no-op default without changing the pipeline.
/// </summary>
/// <remarks>
/// Hooks run inside the import's unit of work and during previews, so they must not write to the
/// database. They may read it through their own short-lived context.
/// </remarks>
public interface IImportCategorizationHook
{
    /// <summary>
    /// Step 3: payee rename rules. May change <see cref="ImportDraft.PayeeName"/>; the pipeline then
    /// resolves (or creates) the payee by that name and applies its default category.
    /// </summary>
    ValueTask RenamePayeesAsync(ImportHookContext context, IReadOnlyList<ImportDraft> drafts, CancellationToken ct);

    /// <summary>
    /// Step 4: categorization rules, then the learner for rows no rule matched. May set
    /// <see cref="ImportDraft.CategoryId"/> (already set when the payee has a default category or the
    /// source chose one), <see cref="ImportDraft.Memo"/> and <see cref="ImportDraft.IsApproved"/>.
    /// </summary>
    ValueTask CategorizeAsync(ImportHookContext context, IReadOnlyList<ImportDraft> drafts, CancellationToken ct);
}

/// <summary>What a hook knows about the batch.</summary>
/// <param name="Source">Where the rows come from.</param>
/// <param name="AccountId">Target account.</param>
/// <param name="IsOnBudget">Whether the target account is on budget (tracking rows keep no category).</param>
/// <param name="IsPreview">True for <see cref="IImportService.PreviewAsync"/>.</param>
public sealed record ImportHookContext(TransactionSource Source, Guid AccountId, bool IsOnBudget, bool IsPreview);

/// <summary>
/// A row the pipeline is about to insert, as seen by <see cref="IImportCategorizationHook"/>.
/// The incoming fields are read-only; the payee, category, memo and approval are the hook's to set.
/// </summary>
public sealed class ImportDraft
{
    /// <summary>Creates a draft for the row at <paramref name="index"/>.</summary>
    public ImportDraft(int index, IncomingTransaction incoming, string normalizedPayee, string payeeName)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        Index = index;
        Incoming = incoming;
        NormalizedPayee = normalizedPayee;
        PayeeName = payeeName;
        Memo = incoming.Memo;
        CategoryId = incoming.CategoryId;
    }

    /// <summary>Position in the batch.</summary>
    public int Index { get; }

    /// <summary>The row as received.</summary>
    public IncomingTransaction Incoming { get; }

    /// <summary>PRD 6.5 normalized payee of the raw descriptor.</summary>
    public string NormalizedPayee { get; }

    /// <summary>Display payee to resolve or create; empty for no payee.</summary>
    public string PayeeName { get; set; }

    /// <summary>Category to set, or null to leave the row uncategorized.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>Memo to store.</summary>
    public string? Memo { get; set; }

    /// <summary>Approval override; null keeps the pipeline default (approved only for manual entry).</summary>
    public bool? IsApproved { get; set; }

    /// <summary>Why the category was chosen (shown in review; e.g. the learner's explanation).</summary>
    public string? CategoryReason { get; set; }
}
