using System.Text.Json;
using Keel.Application.Settings;
using Keel.Application.Stats;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Budgeting;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Recurring;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Stats;

/// <summary>
/// The PRD 4 metrics (ADR 0102), read-only over the open file's audit log and Setting table and over
/// settings.json. Queries are bounded: the first-budget times are two MIN() lookups, the review window reads only
/// the transaction updates of the last 30 days that turned a row approved.
/// </summary>
public sealed class StatsService(IDbContextFactory<KeelDbContext> factory, IAppSettingsStore settings, TimeProvider time) : IStatsService
{
    /// <inheritdoc />
    public Task<StatsReport> GetAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var local = settings.Current.Stats ?? new LocalStats();
            var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
            {
                var firstBudget = await FirstBudgetAsync(db, local, ct).ConfigureAwait(false);
                var review = await ReviewAsync(db, now.AddDays(-StatsReport.ReviewWindowDays), ct).ConfigureAwait(false);
                var log = await DataFileSettings.GetAsync(db, ImportStatsLog.Key, new ImportStatsLog(), ct).ConfigureAwait(false);
                var reimport = new ReimportStat(log.Reimports.Count, log.Reimports.Sum(r => r.Rows), log.Reimports.Sum(r => r.Flagged), log.Reimports.Count == 0 ? null : log.Reimports.Max(r => r.At));
                return new StatsReport(firstBudget, review, reimport, local.ColdStart, local.RegisterScroll, now);
            }
        },
        ct);

    private static async Task<FirstBudgetStat> FirstBudgetAsync(KeelDbContext db, LocalStats local, CancellationToken ct)
    {
        var events = db.AuditEvents.AsNoTracking();
        var firstEvent = await events.MinAsync(e => (DateTime?)e.At, ct).ConfigureAwait(false);
        var firstAssigned = await events
            .Where(e => e.EntityType == BudgetService.AssignmentEntityType && e.AfterJson != null)
            .MinAsync(e => (DateTime?)e.At, ct).ConfigureAwait(false);

        // The first launch counts when it was recorded and precedes this file; files made on an install older than
        // the measurement start at their own first recorded change.
        if (local.FirstLaunchAt is { } launch && (firstEvent is null || launch <= firstEvent))
        {
            return new FirstBudgetStat(launch, FirstBudgetStart.FirstLaunch, firstAssigned);
        }

        return firstEvent is { } created
            ? new FirstBudgetStat(created, FirstBudgetStart.FileCreated, firstAssigned)
            : new FirstBudgetStat(null, FirstBudgetStart.Unknown, firstAssigned);
    }

    // An approval is an audited transaction update that turned IsApproved from false to true (the latest one per
    // transaction in the window). It counts as "without change" when the category and transfer account at approval
    // are what the import pipeline gave the row (its creation audit row), so a change made before approving counts.
    private static async Task<ReviewAccuracyStat> ReviewAsync(KeelDbContext db, DateTime since, CancellationToken ct)
    {
        const string approvedAfter = "\"IsApproved\":true";
        const string unapprovedBefore = "\"IsApproved\":false";
        var rows = await db.AuditEvents.AsNoTracking()
            .Where(e => e.At >= since && e.EntityType == nameof(Transaction) && e.Kind == AuditEventKind.Updated
                && e.AfterJson!.Contains(approvedAfter) && e.BeforeJson!.Contains(unapprovedBefore))
            .OrderBy(e => e.Id)
            .Select(e => new { e.EntityId, e.BeforeJson, e.AfterJson })
            .ToListAsync(ct).ConfigureAwait(false);
        var latest = new Dictionary<string, (string Before, string After)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            latest[row.EntityId] = (row.BeforeJson!, row.AfterJson!);
        }

        var created = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in latest.Keys.Chunk(500))
        {
            var found = await db.AuditEvents.AsNoTracking()
                .Where(e => e.EntityType == nameof(Transaction) && e.Kind == AuditEventKind.Created && chunk.Contains(e.EntityId) && e.AfterJson != null)
                .Select(e => new { e.EntityId, e.AfterJson })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in found)
            {
                created.TryAdd(row.EntityId, row.AfterJson!);
            }
        }

        int approved = 0, unchanged = 0;
        foreach (var (id, (beforeJson, afterJson)) in latest)
        {
            using var after = JsonDocument.Parse(afterJson);
            if (!Imported(after.RootElement))
            {
                continue; // manual and scheduled entries never went through the categorizer
            }

            using var original = JsonDocument.Parse(created.TryGetValue(id, out var insert) ? insert : beforeJson);
            approved++;
            if (Same(original.RootElement, after.RootElement, nameof(Transaction.CategoryId))
                && Same(original.RootElement, after.RootElement, nameof(Transaction.TransferAccountId)))
            {
                unchanged++;
            }
        }

        return new ReviewAccuracyStat(approved, unchanged, since);
    }

    private static bool Imported(JsonElement row) =>
        row.TryGetProperty(nameof(Transaction.Source), out var source) && source.ValueKind == JsonValueKind.String
        && source.GetString() is nameof(TransactionSource.File) or nameof(TransactionSource.Provider);

    private static bool Same(JsonElement a, JsonElement b, string property) =>
        string.Equals(Text(a, property), Text(b, property), StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
