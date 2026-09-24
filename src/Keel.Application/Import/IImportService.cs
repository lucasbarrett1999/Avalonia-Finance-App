using Keel.Domain;

namespace Keel.Application.Import;

/// <summary>
/// The unified import pipeline (F-TXN-1). Manual entry, file import and bank sync all call it,
/// so every source gets the same normalization, dedup (6.5), rules, learner and transfer matching.
/// Implemented in M3.
/// </summary>
public interface IImportService
{
    /// <summary>Classifies every incoming row without writing anything.</summary>
    Task<ImportPreview> PreviewAsync(TransactionSource source, ImportBatch batch, CancellationToken ct);

    /// <summary>Runs the pipeline and commits the batch in one database transaction.</summary>
    Task<ImportSummary> ImportTransactionsAsync(TransactionSource source, ImportBatch batch, CancellationToken ct);
}

/// <summary>A batch of incoming transactions for one account.</summary>
/// <param name="AccountId">Target account.</param>
/// <param name="Transactions">Rows to import.</param>
/// <param name="SyncConnectionId">Connection that produced the batch, for provider-id dedup.</param>
public sealed record ImportBatch(Guid AccountId, IReadOnlyList<IncomingTransaction> Transactions, Guid? SyncConnectionId = null);

/// <summary>One incoming row, already parsed into minor units and a civil date.</summary>
public sealed record IncomingTransaction(
    DateOnly Date,
    long Amount,
    string PayeeRaw,
    string? Memo = null,
    string? ProviderTransactionId = null,
    string? ProviderPendingId = null,
    bool IsPending = false,
    Guid? CategoryId = null);

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

/// <summary>Preview of an import.</summary>
public sealed record ImportPreview(IReadOnlyList<ImportPreviewRow> Rows);

/// <summary>One previewed row.</summary>
public sealed record ImportPreviewRow(IncomingTransaction Incoming, DedupOutcome Outcome, Guid? ExistingTransactionId);

/// <summary>Import summary shown in the status strip.</summary>
public sealed record ImportSummary(
    int Added,
    int Updated,
    int DuplicatesSkipped,
    int MatchedToExisting,
    int TransfersMatched,
    int Uncategorized);
