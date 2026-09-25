namespace Keel.Application.Attachments;

/// <summary>
/// Receipts and documents attached to transactions (F-TXN-8, ADR 0097). Files are stored content-addressed
/// (named by their SHA-256) in the attachments folder next to the budget file; the <c>Attachment</c> row keeps
/// the original file name, the hash and the MIME type. Adding copies the file first and then writes the row as
/// one undoable ledger action; removing only removes the row (undoable), and files no row or recent history
/// refers to are deleted by <see cref="CleanOrphansAsync"/> (the daily maintenance job). Files are not encrypted.
/// </summary>
public interface IAttachmentService
{
    /// <summary>The attachments folder of the open budget file (it may not exist yet), or null before a file is open.</summary>
    string? Folder { get; }

    /// <summary>The attachments of a transaction, oldest first.</summary>
    Task<IReadOnlyList<AttachmentDto>> ListAsync(Guid transactionId, CancellationToken ct);

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into the attachments folder (by hash; an identical file is stored
    /// once) and attaches it to the transaction. Undoable. Throws <see cref="Ledger.LedgerValidationException"/>
    /// when the transaction does not exist and <see cref="IOException"/> when the file cannot be read.
    /// </summary>
    Task<AttachmentDto> AddAsync(Guid transactionId, string sourcePath, CancellationToken ct);

    /// <summary>Removes an attachment from its transaction. Undoable; the stored file stays until it is an orphan.</summary>
    Task RemoveAsync(Guid attachmentId, CancellationToken ct);

    /// <summary>
    /// A read-only copy of the attachment under its original file name in a temporary folder, for opening with
    /// the system's default app (the stored file has no extension and must not be edited in place).
    /// Throws <see cref="Ledger.LedgerValidationException"/> when the row or its file is missing.
    /// </summary>
    Task<string> GetOpenablePathAsync(Guid attachmentId, CancellationToken ct);

    /// <summary>
    /// Deletes stored files that no attachment row (including rows of deleted transactions) and no attachment
    /// change of the last <see cref="OrphanGraceDays"/> days refers to, and files written in the last day are kept,
    /// so undo can always restore a removed attachment. Returns how many files were deleted.
    /// </summary>
    Task<int> CleanOrphansAsync(CancellationToken ct);

    /// <summary>Days an unreferenced file is kept after its last attachment change (undo can bring the row back).</summary>
    const int OrphanGraceDays = 30;
}

/// <summary>An attached file.</summary>
/// <param name="Id">Attachment id.</param>
/// <param name="TransactionId">Transaction.</param>
/// <param name="FileName">Original file name.</param>
/// <param name="Sha256">Lower-case hex SHA-256 (the stored file's name).</param>
/// <param name="MimeType">MIME type from the file extension.</param>
/// <param name="Size">Stored file size in bytes, or null when the file is missing.</param>
public sealed record AttachmentDto(Guid Id, Guid TransactionId, string FileName, string Sha256, string MimeType, long? Size)
{
    /// <summary>Whether the stored file is missing (e.g. deleted outside Keel).</summary>
    public bool IsMissing => Size is null;
}
