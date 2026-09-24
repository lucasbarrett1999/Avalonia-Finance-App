using Keel.Domain;
using Keel.Domain.Import;

namespace Keel.Application.Import;

/// <summary>
/// The unified import pipeline (F-TXN-1). Manual entry, file import and bank sync all call it,
/// so every source gets the same normalization, dedup (6.5), rules, learner and transfer matching.
/// Implemented in M3 (<c>Keel.Infrastructure.Import.ImportService</c>).
/// </summary>
public interface IImportService
{
    /// <summary>Classifies every incoming row without writing anything.</summary>
    Task<ImportPreview> PreviewAsync(TransactionSource source, ImportBatch batch, CancellationToken ct);

    /// <summary>
    /// Runs the pipeline and commits the batch in one database transaction: one audited, undoable
    /// action (<c>LedgerAction.ImportTransactions</c>) and one <c>LedgerChanged</c> message.
    /// </summary>
    Task<ImportSummary> ImportTransactionsAsync(TransactionSource source, ImportBatch batch, CancellationToken ct);
}

/// <summary>A batch of incoming transactions for one account.</summary>
/// <param name="AccountId">Target account.</param>
/// <param name="Transactions">Rows to import.</param>
/// <param name="SyncConnectionId">Connection that produced the batch, for provider-id dedup.</param>
public sealed record ImportBatch(Guid AccountId, IReadOnlyList<IncomingTransaction> Transactions, Guid? SyncConnectionId = null)
{
    /// <summary>A balance the source reported for the account (OFX <c>LEDGERBAL</c>, provider balance);
    /// recorded as a <c>BalanceSnapshot</c> and as the account's reported balance.</summary>
    public StatementBalance? ReportedBalance { get; init; }

    /// <summary>The user's per-row choices from the preview, keyed by row index; rows without an
    /// entry use <see cref="ImportPreviewRow.IncludeByDefault"/> and <see cref="ImportPreviewRow.PairTransferByDefault"/>.</summary>
    public IReadOnlyDictionary<int, ImportRowOverride>? Overrides { get; init; }

    /// <summary>Problems found while reading the source (parse warnings); passed through to the summary.</summary>
    public IReadOnlyList<ImportWarning> Warnings { get; init; } = [];
}

/// <summary>A dated balance stated by the import source.</summary>
/// <param name="Date">Date the balance is as of.</param>
/// <param name="Balance">Balance in minor units.</param>
public sealed record StatementBalance(DateOnly Date, long Balance);

/// <summary>The user's choice for one previewed row.</summary>
/// <param name="Include">Whether the row enters the ledger: inserted, or applied to its matched row.
/// Checking a duplicate inserts it as a new row; unchecking a match or an update leaves the existing row alone.</param>
/// <param name="PairAsTransfer">Whether a detected transfer partner is paired with the row.</param>
public sealed record ImportRowOverride(bool Include, bool PairAsTransfer = true);

/// <summary>One incoming row, already parsed into minor units and a civil date.</summary>
/// <param name="Date">Ledger date.</param>
/// <param name="Amount">Signed minor units; outflows negative.</param>
/// <param name="PayeeRaw">Payee exactly as the source spelled it.</param>
/// <param name="Memo">Memo.</param>
/// <param name="ProviderTransactionId">Provider or file id (FITID, Plaid id).</param>
/// <param name="ProviderPendingId">For a posted row: the provider id of the pending row it replaces.</param>
/// <param name="IsPending">Whether the source marks the row pending.</param>
/// <param name="CategoryId">A category chosen by the source (manual entry); rules and the learner may still set one.</param>
public sealed record IncomingTransaction(
    DateOnly Date,
    long Amount,
    string PayeeRaw,
    string? Memo = null,
    string? ProviderTransactionId = null,
    string? ProviderPendingId = null,
    bool IsPending = false,
    Guid? CategoryId = null) : IImportRecord
{
    /// <summary>Check or reference number.</summary>
    public string? CheckNumber { get; init; }

    /// <summary>The source's own category text (QIF <c>L</c>, a CSV category column); a hint for rules, never applied blindly.</summary>
    public string? CategoryHint { get; init; }

    /// <inheritdoc />
    string? IImportRecord.PendingTransactionId => ProviderPendingId;
}

/// <summary>Dedup decision for a row (6.5).</summary>
public enum DedupOutcome
{
    /// <summary>New row; inserted as unapproved (approved for manual entry).</summary>
    Insert,

    /// <summary>Provider id matched; existing row updated in place.</summary>
    UpdateInPlace,

    /// <summary>Posted row replaced its pending row.</summary>
    PendingToPosted,

    /// <summary>Exact fingerprint match; skipped.</summary>
    DuplicateSkipped,

    /// <summary>Fuzzy match to a manual or scheduled row entered first.</summary>
    MatchedToExisting,
}

/// <summary>Maps the domain's <see cref="DedupDecision"/> (named after PRD 6.5 steps) to the preview outcome.</summary>
public static class DedupOutcomes
{
    /// <summary>
    /// The outcome of a decision. A provider-id match whose date, amount and payee are unchanged is
    /// a duplicate (nothing to update); with changes it is an in-place update.
    /// </summary>
    public static DedupOutcome From(DedupDecision decision, bool hasChanges) => decision switch
    {
        DedupDecision.Insert => DedupOutcome.Insert,
        DedupDecision.UpdateByProviderId => hasChanges ? DedupOutcome.UpdateInPlace : DedupOutcome.DuplicateSkipped,
        DedupDecision.UpdatePendingToPosted => DedupOutcome.PendingToPosted,
        DedupDecision.SkipExactFingerprint => DedupOutcome.DuplicateSkipped,
        DedupDecision.MatchExistingManual => DedupOutcome.MatchedToExisting,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null),
    };
}

/// <summary>Preview of an import.</summary>
public sealed record ImportPreview(IReadOnlyList<ImportPreviewRow> Rows)
{
    /// <summary>Target account.</summary>
    public Guid AccountId { get; init; }

    /// <summary>Problems found while reading the source and while planning.</summary>
    public IReadOnlyList<ImportWarning> Warnings { get; init; } = [];

    /// <summary>The reported balance that the import would record.</summary>
    public StatementBalance? ReportedBalance { get; init; }

    /// <summary>Number of rows with <paramref name="outcome"/>.</summary>
    public int Count(DedupOutcome outcome) => Rows.Count(r => r.Outcome == outcome);
}

/// <summary>One previewed row.</summary>
/// <param name="Incoming">The row as received.</param>
/// <param name="Outcome">What dedup decided.</param>
/// <param name="ExistingTransactionId">The matched existing transaction, if any.</param>
public sealed record ImportPreviewRow(IncomingTransaction Incoming, DedupOutcome Outcome, Guid? ExistingTransactionId)
{
    /// <summary>Position of the row in the batch (the key of <see cref="ImportBatch.Overrides"/>).</summary>
    public int Index { get; init; }

    /// <summary>Normalized payee (PRD 6.5).</summary>
    public string NormalizedPayee { get; init; } = string.Empty;

    /// <summary>The payee the row would get (after rename rules), or empty.</summary>
    public string PayeeName { get; init; } = string.Empty;

    /// <summary>The category the row would get, if any.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>For provider-id matches: whether date, amount or payee differ from the stored row.</summary>
    public bool HasChanges { get; init; }

    /// <summary>Whether the row is included unless the user overrides it (duplicates are not).</summary>
    public bool IncludeByDefault { get; init; }

    /// <summary>Whether the user can change <see cref="IncludeByDefault"/> (not for unchanged provider-id matches, which have nothing to apply).</summary>
    public bool CanOverride { get; init; } = true;

    /// <summary>The other account when a transfer partner was detected (F-TXN-1 step 5).</summary>
    public Guid? TransferAccountId { get; init; }

    /// <summary>The existing transaction the row pairs with as a transfer.</summary>
    public Guid? TransferPartnerId { get; init; }

    /// <summary>Whether another partner was equally close; such pairs are not made unless the user checks them.</summary>
    public bool IsTransferAmbiguous { get; init; }

    /// <summary>Whether the detected transfer is paired unless the user overrides it.</summary>
    public bool PairTransferByDefault { get; init; }

    /// <summary>Whether a transfer partner was detected.</summary>
    public bool HasTransferPartner => TransferPartnerId is not null;
}

/// <summary>Import summary shown in the status strip.</summary>
/// <param name="Added">New transactions inserted.</param>
/// <param name="Updated">Existing rows updated in place (provider id or pending to posted).</param>
/// <param name="DuplicatesSkipped">Rows skipped as duplicates.</param>
/// <param name="MatchedToExisting">Rows matched to a manual or scheduled transaction.</param>
/// <param name="TransfersMatched">Inserted rows paired with a transaction in another account.</param>
/// <param name="Uncategorized">Inserted rows left without a category that need one.</param>
public sealed record ImportSummary(
    int Added,
    int Updated,
    int DuplicatesSkipped,
    int MatchedToExisting,
    int TransfersMatched,
    int Uncategorized)
{
    /// <summary>Rows the user unchecked in the preview.</summary>
    public int SkippedByChoice { get; init; }

    /// <summary>Problems found while reading the source and while importing.</summary>
    public IReadOnlyList<ImportWarning> Warnings { get; init; } = [];

    /// <summary>The reported balance recorded, if any.</summary>
    public StatementBalance? ReportedBalance { get; init; }

    /// <summary>Whether the import wrote anything (and so can be undone).</summary>
    public bool HasChanges => Added + Updated + MatchedToExisting > 0 || ReportedBalance is not null;
}
