using Keel.Application.Import;
using Keel.Domain;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Recurring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Stats;

/// <summary>
/// Wraps the import pipeline to remember, for the Stats page, which files were imported and how many rows of a
/// re-imported file were flagged as duplicates (PRD 4). Only file imports count; recording never fails an import.
/// </summary>
public sealed partial class ImportStatsRecorder(
    IImportService inner,
    IDbContextFactory<KeelDbContext> factory,
    TimeProvider time,
    ILogger<ImportStatsRecorder> logger) : IImportService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public Task<ImportPreview> PreviewAsync(TransactionSource source, ImportBatch batch, CancellationToken ct) => inner.PreviewAsync(source, batch, ct);

    /// <inheritdoc />
    public async Task<ImportSummary> ImportTransactionsAsync(TransactionSource source, ImportBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var summary = await inner.ImportTransactionsAsync(source, batch, ct).ConfigureAwait(false);
        if (source == TransactionSource.File && batch.Transactions.Count > 0)
        {
            await RecordAsync(ImportStatsLog.Fingerprint(batch), batch.Transactions.Count, summary.DuplicatesSkipped).ConfigureAwait(false);
        }

        return summary;
    }

    private async Task RecordAsync(string fingerprint, int rows, int flagged)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
            {
                var log = await DataFileSettings.GetAsync(db, ImportStatsLog.Key, new ImportStatsLog(), CancellationToken.None).ConfigureAwait(false);
                await DataFileSettings.StageAsync(db, ImportStatsLog.Key, log.Record(fingerprint, rows, flagged, time.GetUtcNow().UtcDateTime), CancellationToken.None).ConfigureAwait(false);
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or System.Data.Common.DbException)
        {
            LogRecordFailed(logger, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording import statistics failed")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception);
}
