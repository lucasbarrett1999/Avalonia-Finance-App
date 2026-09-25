using System.Text.Json;
using Keel.Application.Attachments;
using Keel.Application.Files;
using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Domain.Entities;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Attachments;

/// <summary>
/// The attachment store (F-TXN-8, ADR 0097): content-addressed files in the attachments folder next to the
/// budget file (<see cref="IDataDirectory.AttachmentsDirectoryFor"/>, which backups, restore and "Change
/// location" already carry), rows written through <see cref="LedgerWriter"/>. The file is copied before the row
/// is written, so a row never points at a file that is not there; a copy whose row was never written is an
/// orphan the daily clean-up removes.
/// </summary>
public sealed partial class AttachmentService(
    KeelDbContextFactory factory,
    IDataDirectory dataDirectory,
    LedgerWriter writer,
    TimeProvider time,
    ILogger<AttachmentService> logger) : IAttachmentService
{
    /// <inheritdoc />
    public string? Folder => factory.CurrentPath is { } path ? dataDirectory.AttachmentsDirectoryFor(path) : null;

    /// <inheritdoc />
    public Task<IReadOnlyList<AttachmentDto>> ListAsync(Guid transactionId, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var rows = await db.Attachments.AsNoTracking().Where(a => a.TransactionId == transactionId).ToListAsync(ct).ConfigureAwait(false);
                IReadOnlyList<AttachmentDto> list = rows.OrderBy(a => a.Id).Select(ToDto).ToList();
                return list;
            }
        },
        ct);

    /// <inheritdoc />
    public async Task<AttachmentDto> AddAsync(Guid transactionId, string sourcePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var folder = Folder ?? throw new InvalidOperationException("No budget file is open.");
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The file to attach was not found.", sourcePath);
        }

        var sha = await Task.Run(() => StoreAsync(folder, sourcePath, ct), ct).ConfigureAwait(false);
        var fileName = AttachmentFiles.StoredName(sourcePath);
        var row = await writer.RunAsync(
            LedgerAction.AddAttachment,
            async session =>
            {
                var db = session.Db;
                if (!await db.Transactions.AnyAsync(t => t.Id == transactionId, ct).ConfigureAwait(false))
                {
                    throw new LedgerValidationException(LedgerError.TransactionNotFound);
                }

                // The same file on the same transaction is attached once.
                var existing = await db.Attachments.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.TransactionId == transactionId && a.Sha256 == sha, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    return existing;
                }

                var attachment = new Attachment
                {
                    TransactionId = transactionId,
                    FileName = fileName,
                    Sha256 = sha,
                    MimeType = AttachmentFiles.MimeTypeFor(fileName),
                };
                db.Attachments.Add(attachment);
                return attachment;
            },
            ct).ConfigureAwait(false);
        return ToDto(row);
    }

    /// <inheritdoc />
    public Task RemoveAsync(Guid attachmentId, CancellationToken ct) =>
        writer.RunAsync(
            LedgerAction.RemoveAttachment,
            async session =>
            {
                var row = await session.Db.Attachments.SingleOrDefaultAsync(a => a.Id == attachmentId, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.AttachmentNotFound);
                session.Db.Attachments.Remove(row);
                return true;
            },
            ct);

    /// <inheritdoc />
    public Task<string> GetOpenablePathAsync(Guid attachmentId, CancellationToken ct) => Task.Run(
        async () =>
        {
            Attachment row;
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                row = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == attachmentId, ct).ConfigureAwait(false)
                    ?? throw new LedgerValidationException(LedgerError.AttachmentNotFound);
            }

            var stored = StoredPath(row.Sha256);
            if (stored is null || !File.Exists(stored))
            {
                throw new LedgerValidationException(LedgerError.AttachmentFileMissing);
            }

            // A copy under the original name: the OS picks the app by extension, and edits never touch the store.
            var directory = Path.Combine(Path.GetTempPath(), "keel-attachments", row.Sha256[..16]);
            Directory.CreateDirectory(directory);
            var copy = Path.Combine(directory, AttachmentFiles.SafeName(row.FileName, row.Sha256[..12]));
            var info = new FileInfo(copy);
            if (!info.Exists || info.Length != new FileInfo(stored).Length)
            {
                if (info.Exists)
                {
                    File.SetAttributes(copy, FileAttributes.Normal);
                }

                File.Copy(stored, copy, overwrite: true);
                File.SetAttributes(copy, FileAttributes.ReadOnly);
            }

            return copy;
        },
        ct);

    /// <inheritdoc />
    public Task<int> CleanOrphansAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            var folder = Folder;
            if (folder is null || !Directory.Exists(folder))
            {
                return 0;
            }

            var now = time.GetUtcNow().UtcDateTime;
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                keep.UnionWith(await db.Attachments.IgnoreQueryFilters().AsNoTracking().Select(a => a.Sha256).Distinct().ToListAsync(ct).ConfigureAwait(false));

                // Rows removed recently can come back with undo (or redo of an add), so their files stay a while.
                var cutoff = now.AddDays(-IAttachmentService.OrphanGraceDays);
                var recent = await db.AuditEvents.AsNoTracking()
                    .Where(e => e.EntityType == nameof(Attachment) && e.At >= cutoff)
                    .Select(e => new { e.BeforeJson, e.AfterJson })
                    .ToListAsync(ct).ConfigureAwait(false);
                foreach (var e in recent)
                {
                    AddHash(keep, e.BeforeJson);
                    AddHash(keep, e.AfterJson);
                }
            }

            var deleted = 0;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                var isStored = AttachmentFiles.IsHashName(name);
                var isPartial = name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.Ordinal);
                if ((!isStored && !isPartial) || (isStored && keep.Contains(name)) || File.GetLastWriteTimeUtc(file) > now.AddDays(-1))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogDeleteFailed(logger, ex);
                }
            }

            if (deleted > 0)
            {
                LogCleaned(logger, deleted);
            }

            return deleted;
        },
        ct);

    private static void AddHash(HashSet<string> keep, string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(nameof(Attachment.Sha256), out var sha) && sha.ValueKind == JsonValueKind.String && sha.GetString() is { } value)
            {
                keep.Add(value);
            }
        }
        catch (JsonException)
        {
            // An unreadable audit row keeps nothing; the file's own age still protects it for a day.
        }
    }

    // Copies the file in under a temporary name while hashing it, then gives it its hash name (once per content).
    private static async Task<string> StoreAsync(string folder, string sourcePath, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var sha = await AttachmentFiles.CopyWithHashAsync(sourcePath, temp, ct).ConfigureAwait(false);
            var target = Path.Combine(folder, sha);
            if (File.Exists(target))
            {
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            }
            else
            {
                File.Move(temp, target);
            }

            return sha;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private string? StoredPath(string sha) => Folder is { } folder && AttachmentFiles.IsHashName(sha) ? Path.Combine(folder, sha) : null;

    private AttachmentDto ToDto(Attachment row)
    {
        var path = StoredPath(row.Sha256);
        long? size = path is not null && File.Exists(path) ? new FileInfo(path).Length : null;
        return new AttachmentDto(row.Id, row.TransactionId, row.FileName, row.Sha256, row.MimeType, size);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} unreferenced attachment files")]
    private static partial void LogCleaned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete an unreferenced attachment file")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception);
}
