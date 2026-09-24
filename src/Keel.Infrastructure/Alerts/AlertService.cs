using Keel.Application.Alerts;
using Keel.Application.Messaging;
using Keel.Domain;
using Keel.Domain.Alerts;
using Keel.Domain.Entities;
using Keel.Domain.Recurring;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Recurring;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Alerts;

/// <summary>
/// The notification center (F-REC-3): runs <see cref="AlertEvaluator"/> over stored items, the
/// ledger and a detection run's decisions, stores new alerts with their idempotency key (ADR 0032),
/// and marks alerts read or dismissed. Alerts are notifications, not ledger rows: they are written
/// directly (no undo entry, no <see cref="LedgerChanged"/>) and every change publishes
/// <see cref="AlertsChanged"/> with the unread count (ADR 0035).
/// </summary>
public sealed class AlertService(IDbContextFactory<KeelDbContext> factory, IMessageBus bus, TimeProvider time) : IAlertService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public Task<IReadOnlyList<AlertDto>> GetAlertsAsync(bool includeDismissed, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var alerts = await db.Alerts.AsNoTracking()
                    .Where(a => includeDismissed || a.DismissedAt == null)
                    .ToListAsync(ct).ConfigureAwait(false);
                return await MapAsync(db, alerts.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).ToList(), ct).ConfigureAwait(false);
            }
        },
        ct);

    /// <inheritdoc />
    public Task<int> GetUnreadCountAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                return await UnreadAsync(db, ct).ConfigureAwait(false);
            }
        },
        ct);

    /// <inheritdoc />
    public Task MarkReadAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return UpdateAsync(a => ids.Contains(a.Id) && a.ReadAt == null, (a, now) => a.ReadAt = now, ct);
    }

    /// <inheritdoc />
    public Task DismissAsync(Guid id, CancellationToken ct) =>
        UpdateAsync(a => a.Id == id && a.DismissedAt == null, (a, now) =>
        {
            a.DismissedAt = now;
            a.ReadAt ??= now;
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<AlertDto>> EvaluateAsync(AlertEvaluationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(
            async () =>
            {
                // New-item alerts for items created elsewhere: rebuild "create" decisions from the stored rows.
                var decisions = new List<RecurringDecision>();
                if (request.CreatedRecurringItemIds.Count > 0)
                {
                    var db = factory.CreateDbContext();
                    await using (db.ConfigureAwait(false))
                    {
                        var ids = request.CreatedRecurringItemIds.ToList();
                        var items = await db.RecurringItems.AsNoTracking().Where(i => ids.Contains(i.Id)).ToListAsync(ct).ConfigureAwait(false);
                        var payees = await RecurringInputs.NormalizedPayeesAsync(db, items.Select(i => i.PayeeId), ct).ConfigureAwait(false);
                        decisions.AddRange(items.Select(i => CreateDecision(i, payees.GetValueOrDefault(i.PayeeId, string.Empty))));
                    }
                }

                return await EvaluateCoreAsync(request.Today, request.NewTransactionIds, decisions, null, ct).ConfigureAwait(false);
            },
            ct);
    }

    /// <summary>
    /// Evaluates and stores alerts. <paramref name="itemsBefore"/> are the stored items as they were before
    /// the accompanying detection run (loaded here when null); <paramref name="decisions"/> are its decisions.
    /// </summary>
    internal async Task<IReadOnlyList<AlertDto>> EvaluateCoreAsync(
        DateOnly today,
        IReadOnlyCollection<Guid> batchIds,
        IReadOnlyList<RecurringDecision> decisions,
        IReadOnlyList<AlertRecurringItem>? itemsBefore,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        List<Alert> created;
        int unread;
        IReadOnlyList<AlertDto> result;
        try
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                if (itemsBefore is null)
                {
                    var stored = await db.RecurringItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
                    var payees = await RecurringInputs.NormalizedPayeesAsync(db, stored.Select(i => i.PayeeId), ct).ConfigureAwait(false);
                    itemsBefore = stored.Select(i => AlertRecurringItem.From(i, payees.GetValueOrDefault(i.PayeeId, string.Empty))).ToList();
                }

                var rows = await RecurringInputs.LoadAsync(db, RecurringInputs.WindowStart(today), today, batchIds, ct).ConfigureAwait(false);
                var batchSet = batchIds.ToHashSet();
                var history = rows.Select(r => r.Transaction).ToList();
                var batch = history.Where(t => batchSet.Contains(t.Id)).ToList();
                var keys = (await db.Alerts.AsNoTracking().Select(a => a.PayloadJson).ToListAsync(ct).ConfigureAwait(false))
                    .Select(json => AlertPayload.FromJson(json)?.Key)
                    .OfType<string>()
                    .ToHashSet(StringComparer.Ordinal);

                var proposals = AlertEvaluator.Evaluate(new AlertEvaluationInput(today, itemsBefore, history, batch, decisions, keys));
                var now = time.GetUtcNow().UtcDateTime;
                created = proposals.Select(p => p.ToEntity(now)).ToList();
                if (created.Count > 0)
                {
                    db.Alerts.AddRange(created);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                unread = await UnreadAsync(db, ct).ConfigureAwait(false);
                result = await MapAsync(db, created, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (created.Count > 0)
        {
            bus.Publish(new AlertsChanged(unread));
        }

        return result;
    }

    /// <summary>A "create" decision for a stored item (new-item alerts for items created outside a detection run).</summary>
    internal static RecurringDecision CreateDecision(RecurringItem item, string normalizedPayee)
    {
        var detection = new DetectedRecurringItem(
            new RecurringGroupKey(normalizedPayee, item.AccountId ?? Guid.Empty),
            item.Cadence,
            item.ExpectedAmount,
            item.AmountTolerance,
            item.IsVariableAmount,
            item.NextExpectedDate,
            item.LastSeenDate,
            item.LastSeenDate,
            item.Confidence,
            item.Confidence,
            0,
            RecurringSchedule.InferRule(item.Cadence, item.NextExpectedDate, item.LastSeenDate),
            false,
            [],
            []);
        return new RecurringDecision(RecurringDecisionKind.Create, RecurringDecisionReason.NewPattern, detection, item.Id, null, RecurringItemValues.From(item), []);
    }

    private static Task<int> UnreadAsync(KeelDbContext db, CancellationToken ct) =>
        db.Alerts.AsNoTracking().CountAsync(a => a.ReadAt == null && a.DismissedAt == null, ct);

    private Task UpdateAsync(System.Linq.Expressions.Expression<Func<Alert, bool>> filter, Action<Alert, DateTime> change, CancellationToken ct) => Task.Run(
        async () =>
        {
            int unread;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    var rows = await db.Alerts.Where(filter).ToListAsync(ct).ConfigureAwait(false);
                    if (rows.Count == 0)
                    {
                        return;
                    }

                    var now = time.GetUtcNow().UtcDateTime;
                    rows.ForEach(a => change(a, now));
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    unread = await UnreadAsync(db, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }

            bus.Publish(new AlertsChanged(unread));
        },
        ct);

    private static async Task<IReadOnlyList<AlertDto>> MapAsync(KeelDbContext db, IReadOnlyList<Alert> alerts, CancellationToken ct)
    {
        if (alerts.Count == 0)
        {
            return [];
        }

        var currency = await M5Lookups.CurrencyAsync(db, ct).ConfigureAwait(false);
        var itemIds = alerts.Select(a => a.RecurringItemId).OfType<Guid>().Distinct().ToList();
        var itemPayees = itemIds.Count == 0
            ? []
            : await db.RecurringItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.PayeeId, ct).ConfigureAwait(false);
        var txnIds = alerts.Select(a => a.TransactionId).OfType<Guid>().Distinct().ToList();
        var txnPayees = txnIds.Count == 0
            ? []
            : await db.Transactions.IgnoreQueryFilters().AsNoTracking().Where(t => txnIds.Contains(t.Id))
                .Select(t => new { t.Id, t.PayeeId, t.PayeeRaw }).ToDictionaryAsync(t => t.Id, ct).ConfigureAwait(false);
        var names = await M5Lookups.PayeeNamesAsync(
            db,
            itemPayees.Values.Concat(txnPayees.Values.Select(t => t.PayeeId).OfType<Guid>()),
            ct).ConfigureAwait(false);

        return alerts.Select(a =>
        {
            var payload = AlertPayload.FromJson(a.PayloadJson);
            string? payee = null;
            if (a.RecurringItemId is { } item && itemPayees.TryGetValue(item, out var itemPayee))
            {
                payee = names.GetValueOrDefault(itemPayee);
            }

            if (payee is null && a.TransactionId is { } txn && txnPayees.TryGetValue(txn, out var row))
            {
                payee = row.PayeeId is { } pid ? names.GetValueOrDefault(pid) : row.PayeeRaw;
            }

            payee ??= payload?.Payee;
            return new AlertDto(
                a.Id,
                a.Kind,
                a.RecurringItemId,
                a.TransactionId,
                payee,
                payload?.Amount is { } amount ? new Money(amount, currency) : null,
                payload?.PreviousAmount is { } previous ? new Money(previous, currency) : null,
                payload?.IncreaseBasisPoints is { } bp ? bp / 100m : null,
                payload?.ExpectedDate ?? payload?.NextExpectedDate,
                payload?.DaysLate,
                a.CreatedAt,
                a.ReadAt,
                a.DismissedAt);
        }).ToList();
    }
}
