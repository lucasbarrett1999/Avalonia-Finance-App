using Keel.Domain;

namespace Keel.Application.Ledger;

/// <summary>
/// Transaction use cases (F-ACC-2..5): add, edit, soft-delete, restore, bulk actions, transfers
/// kept in sync, splits, and reconciliation. Every mutation writes audit events, joins the undo
/// stack, and publishes <see cref="Messaging.LedgerChanged"/>.
/// </summary>
public interface ITransactionService
{
    /// <summary>Gets one transaction (including soft-deleted ones), or null.</summary>
    Task<TransactionDto?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Creates (when <see cref="SaveTransactionRequest.Id"/> is null) or updates a transaction.
    /// A transfer creates or updates its counterpart in the other account; splits replace the
    /// category. Throws <see cref="LedgerValidationException"/> when a rule is violated.
    /// </summary>
    Task<TransactionDto> SaveAsync(SaveTransactionRequest request, CancellationToken ct);

    /// <summary>Soft-deletes transactions (and the other side of transfers).</summary>
    Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Restores soft-deleted transactions (and the other side of transfers).</summary>
    Task<int> RestoreAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Permanently removes transactions that are already soft-deleted.</summary>
    Task<int> PurgeDeletedAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Sets the cleared status (Uncleared or Cleared); reconciled rows are left alone.</summary>
    Task<int> SetClearedAsync(IReadOnlyCollection<Guid> ids, bool cleared, CancellationToken ct);

    /// <summary>Toggles Uncleared/Cleared for one transaction and returns the new status.</summary>
    Task<TransactionStatus> ToggleClearedAsync(Guid id, CancellationToken ct);

    /// <summary>Marks transactions approved.</summary>
    Task<int> ApproveAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>
    /// Sets the category of transactions. Split parents and transfers that must stay
    /// uncategorized are skipped; returns how many changed.
    /// </summary>
    Task<int> CategorizeAsync(IReadOnlyCollection<Guid> ids, Guid categoryId, CancellationToken ct);

    /// <summary>Moves transactions to another account (transfers keep their pair consistent).</summary>
    Task<int> MoveToAccountAsync(IReadOnlyCollection<Guid> ids, Guid accountId, CancellationToken ct);

    /// <summary>Cleared balance as of the statement date and the difference to the statement (F-ACC-3).</summary>
    Task<ReconciliationStatus> GetReconciliationStatusAsync(Guid accountId, DateOnly statementDate, long statementBalance, CancellationToken ct);

    /// <summary>
    /// Finishes a reconciliation: optionally records a balance-adjustment transaction for a
    /// non-zero difference, locks the cleared transactions as reconciled, and records the event.
    /// </summary>
    Task<ReconciliationResult> FinishReconciliationAsync(FinishReconciliationRequest request, CancellationToken ct);
}

/// <summary>A split line (F-ACC-5).</summary>
/// <param name="CategoryId">Category.</param>
/// <param name="Memo">Memo.</param>
/// <param name="Amount">Amount in minor units (sign as the parent).</param>
public sealed record SplitLine(Guid? CategoryId, string? Memo, long Amount);

/// <summary>Input for <see cref="ITransactionService.SaveAsync"/>.</summary>
/// <param name="Id">Existing transaction to update, or null to create.</param>
/// <param name="AccountId">Account.</param>
/// <param name="Date">Date.</param>
/// <param name="Amount">Amount in minor units; outflows negative.</param>
/// <param name="Payee">Payee name (get-or-create); ignored for transfers.</param>
/// <param name="CategoryId">Category; ignored when split or for on-budget transfers.</param>
/// <param name="Memo">Memo.</param>
/// <param name="Status">Cleared status (kept for existing reconciled rows).</param>
/// <param name="IsApproved">Approval flag; manual entry is approved.</param>
/// <param name="TransferAccountId">Other account of a transfer, or null.</param>
/// <param name="Splits">Split lines, or null/empty for an unsplit transaction.</param>
/// <param name="Tags">The transaction's tags after the save (new names create tags in the same action), or null to keep them (F-TXN-8).</param>
public sealed record SaveTransactionRequest(
    Guid? Id,
    Guid AccountId,
    DateOnly Date,
    long Amount,
    string? Payee,
    Guid? CategoryId,
    string? Memo,
    TransactionStatus Status = TransactionStatus.Uncleared,
    bool IsApproved = true,
    Guid? TransferAccountId = null,
    IReadOnlyList<SplitLine>? Splits = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>A transaction for editing.</summary>
public sealed record TransactionDto(
    Guid Id,
    Guid AccountId,
    DateOnly Date,
    Guid? PayeeId,
    string Payee,
    Guid? CategoryId,
    string? Memo,
    long Amount,
    TransactionStatus Status,
    bool IsApproved,
    TransactionSource Source,
    Guid? TransferAccountId,
    Guid? TransferPairId,
    IReadOnlyList<SplitLine> Splits,
    bool IsDeleted)
{
    /// <summary>Tag names by name (F-TXN-8).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>Where a reconciliation stands.</summary>
public sealed record ReconciliationStatus(
    Guid AccountId,
    DateOnly StatementDate,
    long StatementBalance,
    long ClearedBalance,
    long Difference,
    int ClearedCount,
    int UnclearedCount);

/// <summary>Input for <see cref="ITransactionService.FinishReconciliationAsync"/>.</summary>
/// <param name="AccountId">Account.</param>
/// <param name="StatementDate">Statement date.</param>
/// <param name="StatementBalance">Statement balance in minor units.</param>
/// <param name="CreateAdjustment">Record a balance adjustment when the difference is not zero.</param>
public sealed record FinishReconciliationRequest(Guid AccountId, DateOnly StatementDate, long StatementBalance, bool CreateAdjustment);

/// <summary>Outcome of a finished reconciliation.</summary>
public sealed record ReconciliationResult(Guid ReconciliationId, int LockedCount, Guid? AdjustmentTransactionId, long Adjustment);
