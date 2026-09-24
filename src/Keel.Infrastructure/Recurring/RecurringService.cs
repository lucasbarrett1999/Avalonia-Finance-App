using Keel.Application.Budget;
using Keel.Application.Messaging;
using Keel.Application.Recurring;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Alerts;
using Keel.Domain.Entities;
using Keel.Domain.Import;
using Keel.Domain.Recurring;
using Keel.Infrastructure.Alerts;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Recurring;

/// <summary>
/// Recurring items and the Bills screen (F-REC-1, F-REC-2, F-REC-4). Detection loads the ledger,
/// runs <see cref="RecurringDetector"/> and <see cref="RecurringReconciler"/>, applies the decisions
/// through <see cref="LedgerWriter"/> (audited; automatic runs are not on the user's undo stack),
/// then evaluates alerts with the items as they were before the run (ADR 0032). User actions
/// (confirm, pause, dismiss, edit, create) are undoable ledger actions. Every change publishes
/// <see cref="RecurringChanged"/>.
/// </summary>
public sealed class RecurringService(
    IDbContextFactory<KeelDbContext> factory,
    LedgerWriter writer,
    AlertService alerts,
    IBudgetService budget,
    IMessageBus bus,
    TimeProvider time) : IRecurringService
{
    /// <summary>Months of history shown in the detail panel's amount chart.</summary>
    public const int HistoryMonths = 24;

    private readonly SemaphoreSlim _detectGate = new(1, 1);

    /// <inheritdoc />
    public Task<RecurringDetectionSummary> DetectAsync(DateOnly asOf, CancellationToken ct) =>
        Task.Run(() => DetectCoreAsync(asOf, [], ct), ct);

    /// <inheritdoc />
    public Task<RecurringDetectionSummary> DetectAfterImportAsync(DateOnly asOf, IReadOnlyCollection<Guid> newTransactionIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(newTransactionIds);
        return Task.Run(() => DetectCoreAsync(asOf, newTransactionIds, ct), ct);
    }

    /// <inheritdoc />
    public Task<RecurringDetectionSummary?> DetectNewImportsAsync(DateOnly today, CancellationToken ct) => Task.Run(
        async () =>
        {
            List<Guid> imported;
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var watermark = await DataFileSettings.GetAsync<long?>(db, DataFileSettings.ImportWatermark, null, ct).ConfigureAwait(false);
                var max = await db.Database.SqlQueryRaw<long>("SELECT COALESCE(MAX(rowid), 0) AS \"Value\" FROM \"AuditEvents\"").SingleAsync(ct).ConfigureAwait(false);
                if (watermark is { } w && max <= w)
                {
                    return null;
                }

                var created = watermark is { } from
                    ? await db.Database.SqlQueryRaw<string>(
                        "SELECT \"EntityId\" AS \"Value\" FROM \"AuditEvents\" WHERE rowid > {0} AND rowid <= {1} AND \"EntityType\" = 'Transaction' AND \"Kind\" = 'Created'",
                        from,
                        max).ToListAsync(ct).ConfigureAwait(false)
                    : [];
                var ids = created.Select(e => Guid.TryParse(e, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();
                imported = [];
                foreach (var chunk in ids.Chunk(500))
                {
                    imported.AddRange(await db.Transactions.AsNoTracking()
                        .Where(t => chunk.Contains(t.Id) && (t.Source == TransactionSource.File || t.Source == TransactionSource.Provider))
                        .Select(t => t.Id).ToListAsync(ct).ConfigureAwait(false));
                }

                await DataFileSettings.StageAsync<long?>(db, DataFileSettings.ImportWatermark, max, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return imported.Count == 0 ? null : (RecurringDetectionSummary?)await DetectCoreAsync(today, imported, ct).ConfigureAwait(false);
        },
        ct);

    /// <inheritdoc />
    public Task<RecurringDetectionSummary?> RunDailyAsync(DateOnly today, CancellationToken ct) => Task.Run(
        async () =>
        {
            var last = await GetLastDetectionDateAsync(ct).ConfigureAwait(false);
            return last is { } l && l >= today ? null : (RecurringDetectionSummary?)await DetectCoreAsync(today, [], ct).ConfigureAwait(false);
        },
        ct);

    /// <inheritdoc />
    public Task<DateOnly?> GetLastDetectionDateAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                return await DataFileSettings.GetAsync<DateOnly?>(db, DataFileSettings.LastDetection, null, ct).ConfigureAwait(false);
            }
        },
        ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<RecurringItemDto>> GetItemsAsync(RecurringItemFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Task.Run(
            async () =>
            {
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    var statuses = filter.Statuses.Count == 0
                        ? Enum.GetValues<RecurringStatus>().Where(s => s != RecurringStatus.Dismissed).ToList()
                        : filter.Statuses.ToList();
                    var query = db.RecurringItems.AsNoTracking().Where(i => statuses.Contains(i.Status));
                    if (filter.AccountId is { } account)
                    {
                        query = query.Where(i => i.AccountId == account);
                    }

                    var items = await query.ToListAsync(ct).ConfigureAwait(false);
                    items = filter.Kind switch
                    {
                        RecurringItemKind.Subscriptions => items.Where(i => i.ExpectedAmount < 0 && i.IsSubscription).ToList(),
                        RecurringItemKind.Bills => items.Where(i => i.ExpectedAmount < 0 && !i.IsSubscription).ToList(),
                        RecurringItemKind.Income => items.Where(i => i.ExpectedAmount >= 0).ToList(),
                        _ => items,
                    };
                    var dtos = await MapAsync(db, items, ct).ConfigureAwait(false);
                    return (IReadOnlyList<RecurringItemDto>)dtos.OrderBy(d => d.NextExpectedDate).ThenBy(d => d.PayeeName, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
            },
            ct);
    }

    /// <inheritdoc />
    public Task<RecurringItemDetailDto?> GetItemAsync(Guid id, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var item = await db.RecurringItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct).ConfigureAwait(false);
                if (item is null)
                {
                    return null;
                }

                var dto = (await MapAsync(db, [item], ct).ConfigureAwait(false))[0];
                var today = M5Lookups.Today(time);
                var history = await HistoryAsync(db, [item], today.AddMonths(-HistoryMonths), today, ct).ConfigureAwait(false);
                var occurrences = history.GetValueOrDefault(item.Id) ?? [];
                var windowStart = RecurringInputs.WindowStart(today);
                var dates = occurrences.Where(o => o.Date >= windowStart).Select(o => o.Date).ToList();
                var scores = dates.Count >= 2 ? RecurringDetector.ScoreCadences(dates) : [];
                return new RecurringItemDetailDto(dto, occurrences, scores);
            }
        },
        ct);

    /// <inheritdoc />
    public Task<RecurringTotalsDto> GetTotalsAsync(bool includeUnconfirmed, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var items = await db.RecurringItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
                var currency = await M5Lookups.CurrencyAsync(db, ct).ConfigureAwait(false);
                var totals = RecurringMath.Totals(items.Select(RecurringTotalsItem.From), includeUnconfirmed);
                return new RecurringTotalsDto(
                    new Money(totals.MonthlyOutflow, currency),
                    new Money(totals.MonthlyInflow, currency),
                    new Money(totals.SubscriptionsMonthly, currency),
                    new Money(totals.SubscriptionsYearly, currency),
                    new Money(totals.BillsMonthly, currency),
                    new Money(totals.BillsYearly, currency),
                    totals.ItemCount);
            }
        },
        ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<RecurringOccurrenceDto>> GetOccurrencesAsync(DateOnly from, DateOnly to, CancellationToken ct) => Task.Run(
        async () =>
        {
            if (to < from)
            {
                return (IReadOnlyList<RecurringOccurrenceDto>)[];
            }

            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var items = await db.RecurringItems.AsNoTracking()
                    .Where(i => i.Status == RecurringStatus.Active || i.Status == RecurringStatus.Detected)
                    .ToListAsync(ct).ConfigureAwait(false);
                var currency = await M5Lookups.CurrencyAsync(db, ct).ConfigureAwait(false);
                var today = M5Lookups.Today(time);
                var actual = await HistoryAsync(db, items, from, to < today ? to : today, ct).ConfigureAwait(false);
                var result = new List<RecurringOccurrenceDto>();
                foreach (var item in items)
                {
                    var past = actual.GetValueOrDefault(item.Id) ?? [];
                    result.AddRange(past);
                    var latestActual = past.Count > 0 ? past.Max(p => p.Date) : DateOnly.MinValue;
                    foreach (var date in Rule(item).Occurrences(item.NextExpectedDate, from, to))
                    {
                        if (date > latestActual)
                        {
                            result.Add(new RecurringOccurrenceDto(item.Id, date, new Money(item.ExpectedAmount, currency), null, true));
                        }
                    }
                }

                return (IReadOnlyList<RecurringOccurrenceDto>)result.OrderBy(o => o.Date).ThenBy(o => o.Amount.Amount).ToList();
            }
        },
        ct);

    /// <inheritdoc />
    public Task ConfirmAsync(Guid id, CancellationToken ct) =>
        MutateAsync(id, LedgerAction.ConfirmRecurring, item => item.Status = RecurringStatus.Active, ct);

    /// <inheritdoc />
    public Task PauseAsync(Guid id, CancellationToken ct) =>
        MutateAsync(id, LedgerAction.PauseRecurring, item => item.Status = RecurringStatus.Paused, ct);

    /// <inheritdoc />
    public Task ResumeAsync(Guid id, CancellationToken ct) =>
        MutateAsync(id, LedgerAction.ResumeRecurring, item => item.Status = RecurringStatus.Active, ct);

    /// <inheritdoc />
    public async Task DismissAsync(Guid id, CancellationToken ct)
    {
        await MutateAsync(id, LedgerAction.DismissRecurring, item => item.Status = RecurringStatus.Dismissed, ct).ConfigureAwait(false);
        await Task.Run(
            async () =>
            {
                // Dismissing again after a re-enable turns detection for the payee off again.
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    var payeeId = await db.RecurringItems.AsNoTracking().Where(i => i.Id == id).Select(i => i.PayeeId).SingleAsync(ct).ConfigureAwait(false);
                    var normalized = (await RecurringInputs.NormalizedPayeesAsync(db, [payeeId], ct).ConfigureAwait(false)).GetValueOrDefault(payeeId);
                    var reenabled = await DataFileSettings.GetAsync<List<string>>(db, DataFileSettings.ReenabledPayees, [], ct).ConfigureAwait(false);
                    if (normalized is not null && reenabled.Remove(normalized))
                    {
                        await DataFileSettings.StageAsync(db, DataFileSettings.ReenabledPayees, reenabled, ct).ConfigureAwait(false);
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    }
                }
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReenableDetectionAsync(Guid payeeId, CancellationToken ct)
    {
        await Task.Run(
            async () =>
            {
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    var normalized = (await RecurringInputs.NormalizedPayeesAsync(db, [payeeId], ct).ConfigureAwait(false)).GetValueOrDefault(payeeId)
                        ?? throw new InvalidOperationException("The payee does not exist.");
                    var reenabled = await DataFileSettings.GetAsync<List<string>>(db, DataFileSettings.ReenabledPayees, [], ct).ConfigureAwait(false);
                    if (!reenabled.Contains(normalized, StringComparer.Ordinal))
                    {
                        reenabled.Add(normalized);
                        await DataFileSettings.StageAsync(db, DataFileSettings.ReenabledPayees, reenabled, ct).ConfigureAwait(false);
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    }
                }
            },
            ct).ConfigureAwait(false);
        await DetectAsync(M5Lookups.Today(time), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RecurringItemDto> CreateAsync(RecurringItemEdit item, CancellationToken ct)
    {
        Validate(item);
        var id = await writer.RunAsync(
            LedgerAction.CreateRecurring,
            async session =>
            {
                await EnsureReferencesAsync(session.Db, item, ct).ConfigureAwait(false);
                var lastSeen = await session.Db.Transactions.AsNoTracking()
                    .Where(t => t.PayeeId == item.PayeeId && (item.AccountId == null || t.AccountId == item.AccountId) && t.Date <= item.NextExpectedDate)
                    .OrderByDescending(t => t.Date)
                    .Select(t => (DateOnly?)t.Date)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                var entity = new RecurringItem
                {
                    PayeeId = item.PayeeId,
                    Status = RecurringStatus.Active,
                    Confidence = 1,
                    LastSeenDate = lastSeen ?? DateOnly.MinValue,
                };
                Apply(entity, item);
                session.Db.RecurringItems.Add(entity);
                return entity.Id;
            },
            ct).ConfigureAwait(false);
        bus.Publish(new RecurringChanged([id]));
        return await GetDtoAsync(id, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RecurringItemDto> UpdateAsync(Guid id, RecurringItemEdit item, CancellationToken ct)
    {
        Validate(item);
        await writer.RunAsync(
            LedgerAction.EditRecurring,
            async session =>
            {
                await EnsureReferencesAsync(session.Db, item, ct).ConfigureAwait(false);
                var entity = await LoadAsync(session.Db, id, ct).ConfigureAwait(false);
                entity.PayeeId = item.PayeeId;
                Apply(entity, item);
                return true;
            },
            ct).ConfigureAwait(false);
        bus.Publish(new RecurringChanged([id]));
        return await GetDtoAsync(id, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CreateTargetAsync(Guid id, CancellationToken ct)
    {
        var item = await Task.Run(
            async () =>
            {
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    return await db.RecurringItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The recurring item does not exist.");
                }
            },
            ct).ConfigureAwait(false);
        if (item.CategoryId is not { } category)
        {
            throw new InvalidOperationException("The recurring item has no category.");
        }

        var target = RecurringMath.CreateSetAsideTarget(category, item.ExpectedAmount, item.Cadence);
        await budget.SetTargetAsync(new TargetDto(category, target.Type, target.Amount), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<SubscriptionDesignations> GetSubscriptionDesignationsAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                return await DesignationsAsync(db, ct).ConfigureAwait(false);
            }
        },
        ct);

    /// <inheritdoc />
    public async Task SetSubscriptionDesignationsAsync(SubscriptionDesignations designations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designations);
        await Task.Run(
            async () =>
            {
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    await DataFileSettings.StageAsync(db, DataFileSettings.SubscriptionGroups, designations.GroupIds.Distinct().ToList(), ct).ConfigureAwait(false);
                    await DataFileSettings.StageAsync(db, DataFileSettings.SubscriptionTags, designations.TagIds.Distinct().ToList(), ct).ConfigureAwait(false);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);

        var changed = await Task.Run(
            () => writer.RunCoreAsync(
                LedgerAction.EditRecurring,
                async session =>
                {
                    var hints = await HintsAsync(session.Db, ct).ConfigureAwait(false);
                    var items = await session.Db.RecurringItems.ToListAsync(ct).ConfigureAwait(false);
                    var tagged = await TaggedGroupsAsync(session.Db, hints, ct).ConfigureAwait(false);
                    var ids = new List<Guid>();
                    foreach (var item in items)
                    {
                        var tags = tagged.Contains((item.PayeeId, item.AccountId)) || tagged.Contains((item.PayeeId, null)) ? hints.SubscriptionTagIds : (IEnumerable<Guid>)[];
                        var isSubscription = SubscriptionClassifier.IsSubscription(item.CategoryId, tags, hints);
                        if (item.IsSubscription != isSubscription)
                        {
                            item.IsSubscription = isSubscription;
                            ids.Add(item.Id);
                        }
                    }

                    return ids;
                },
                LedgerWriter.Recording.None,
                ct),
            ct).ConfigureAwait(false);
        bus.Publish(new RecurringChanged(changed));
    }

    /// <inheritdoc />
    public async Task LinkScheduledAsync(Guid id, Guid? scheduledTransactionId, CancellationToken ct) =>
        await MutateAsync(id, LedgerAction.EditRecurring, item => item.ScheduledTransactionId = scheduledTransactionId, ct).ConfigureAwait(false);

    private static void Validate(RecurringItemEdit item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ExpectedAmount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(item), "The expected amount must not be zero.");
        }
    }

    private static void Apply(RecurringItem entity, RecurringItemEdit item)
    {
        entity.AccountId = item.AccountId;
        entity.Cadence = item.Cadence;
        entity.ExpectedAmount = item.ExpectedAmount;
        entity.AmountTolerance = RecurringDetector.Tolerance(item.ExpectedAmount);
        entity.NextExpectedDate = item.NextExpectedDate;
        entity.CategoryId = item.CategoryId;
        entity.IsSubscription = item.IsSubscription;
    }

    private static async Task EnsureReferencesAsync(KeelDbContext db, RecurringItemEdit item, CancellationToken ct)
    {
        if (!await db.Payees.AnyAsync(p => p.Id == item.PayeeId, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The payee does not exist.");
        }

        if (item.AccountId is { } account && !await db.Accounts.AnyAsync(a => a.Id == account, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The account does not exist.");
        }

        if (item.CategoryId is { } category && !await db.Categories.AnyAsync(c => c.Id == category, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The category does not exist.");
        }
    }

    private static async Task<RecurringItem> LoadAsync(KeelDbContext db, Guid id, CancellationToken ct) =>
        await db.RecurringItems.SingleOrDefaultAsync(i => i.Id == id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The recurring item does not exist.");

    private static Keel.Domain.Scheduling.RecurrenceRule Rule(RecurringItem item) =>
        RecurringSchedule.InferRule(item.Cadence, item.NextExpectedDate, item.LastSeenDate);

    private static async Task<SubscriptionDesignations> DesignationsAsync(KeelDbContext db, CancellationToken ct) => new(
        await DataFileSettings.GetAsync<List<Guid>>(db, DataFileSettings.SubscriptionGroups, [], ct).ConfigureAwait(false),
        await DataFileSettings.GetAsync<List<Guid>>(db, DataFileSettings.SubscriptionTags, [], ct).ConfigureAwait(false));

    private static async Task<SubscriptionHints> HintsAsync(KeelDbContext db, CancellationToken ct)
    {
        var designations = await DesignationsAsync(db, ct).ConfigureAwait(false);
        var groups = designations.GroupIds.ToList();
        var categories = groups.Count == 0
            ? []
            : await db.Categories.AsNoTracking().Where(c => groups.Contains(c.GroupId)).ToListAsync(ct).ConfigureAwait(false);
        return SubscriptionHints.Build(categories, groups, designations.TagIds);
    }

    // (payee, account) pairs with at least one transaction carrying a subscription tag.
    private static async Task<HashSet<(Guid PayeeId, Guid? AccountId)>> TaggedGroupsAsync(KeelDbContext db, SubscriptionHints hints, CancellationToken ct)
    {
        if (hints.SubscriptionTagIds.Count == 0)
        {
            return [];
        }

        var tags = hints.SubscriptionTagIds.ToList();
        var rows = await (
            from tt in db.TransactionTags.AsNoTracking()
            join t in db.Transactions.AsNoTracking() on tt.TransactionId equals t.Id
            where tags.Contains(tt.TagId) && t.PayeeId != null
            select new { PayeeId = t.PayeeId!.Value, t.AccountId }).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var set = new HashSet<(Guid, Guid?)>();
        foreach (var r in rows)
        {
            set.Add((r.PayeeId, r.AccountId));
            set.Add((r.PayeeId, null));
        }

        return set;
    }

    private async Task MutateAsync(Guid id, LedgerAction action, Action<RecurringItem> change, CancellationToken ct)
    {
        await writer.RunAsync(
            action,
            async session =>
            {
                change(await LoadAsync(session.Db, id, ct).ConfigureAwait(false));
                return true;
            },
            ct).ConfigureAwait(false);
        bus.Publish(new RecurringChanged([id]));
    }

    private async Task<RecurringItemDto> GetDtoAsync(Guid id, CancellationToken ct) =>
        (await GetItemAsync(id, ct).ConfigureAwait(false))?.Item ?? throw new InvalidOperationException("The recurring item does not exist.");

    private async Task<RecurringDetectionSummary> DetectCoreAsync(DateOnly asOf, IReadOnlyCollection<Guid> batchIds, CancellationToken ct)
    {
        await _detectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<RecurringLedgerRow> rows;
            List<RecurringItem> stored;
            Dictionary<Guid, string> normalized;
            List<string> reenabled;
            SubscriptionHints hints;
            HashSet<(Guid, Guid?)> tagged;
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                rows = await RecurringInputs.LoadAsync(db, RecurringInputs.WindowStart(asOf), asOf, null, ct).ConfigureAwait(false);
                stored = await db.RecurringItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
                normalized = await RecurringInputs.NormalizedPayeesAsync(db, stored.Select(i => i.PayeeId), ct).ConfigureAwait(false);
                reenabled = await DataFileSettings.GetAsync<List<string>>(db, DataFileSettings.ReenabledPayees, [], ct).ConfigureAwait(false);
                hints = await HintsAsync(db, ct).ConfigureAwait(false);
                tagged = await TaggedGroupsAsync(db, hints, ct).ConfigureAwait(false);
            }

            var existing = stored.Select(i => new ExistingRecurringItem(i, normalized.GetValueOrDefault(i.PayeeId, string.Empty))).ToList();
            var itemsBefore = stored.Select(i => AlertRecurringItem.From(i, normalized.GetValueOrDefault(i.PayeeId, string.Empty))).ToList();
            var detections = RecurringDetector.Detect(rows.Select(r => r.Transaction), asOf);
            var decisions = RecurringReconciler.Reconcile(detections, existing, reenabled.ToHashSet(StringComparer.Ordinal));
            var byId = rows.ToDictionary(r => r.Transaction.Id);
            var toApply = decisions.Where(d => d.Kind != RecurringDecisionKind.NoOp).ToList();
            if (toApply.Count > 0)
            {
                await writer.RunCoreAsync(
                    LedgerAction.DetectRecurring,
                    async session =>
                    {
                        var updateIds = toApply.Where(d => d.Kind == RecurringDecisionKind.Update).Select(d => d.ItemId!.Value).ToList();
                        var tracked = await session.Db.RecurringItems.Where(i => updateIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct).ConfigureAwait(false);
                        foreach (var decision in toApply)
                        {
                            if (decision.Kind == RecurringDecisionKind.Update)
                            {
                                decision.After!.ApplyTo(tracked[decision.ItemId!.Value]);
                                continue;
                            }

                            var occurrences = decision.Detection.TransactionIds.Select(id => byId[id]).ToList();
                            var latest = occurrences[^1];
                            var item = decision.After!.ToNewItem(decision.ItemId!.Value, latest.PayeeId);
                            item.CategoryId = occurrences
                                .Where(o => o.CategoryId is not null)
                                .GroupBy(o => o.CategoryId)
                                .OrderByDescending(g => g.Count())
                                .ThenByDescending(g => g.Max(o => o.Transaction.Date))
                                .Select(g => g.Key)
                                .FirstOrDefault();
                            var tags = tagged.Contains((latest.PayeeId, latest.Transaction.AccountId)) ? hints.SubscriptionTagIds : (IEnumerable<Guid>)[];
                            item.IsSubscription = SubscriptionClassifier.IsSubscription(item.CategoryId, tags, hints);
                            session.Db.RecurringItems.Add(item);
                        }

                        return true;
                    },
                    LedgerWriter.Recording.None,
                    ct).ConfigureAwait(false);
            }

            // Re-enabled payees are consumed once their item came back.
            var consumed = decisions.Where(d => d.Reason == RecurringDecisionReason.Reenabled).Select(d => d.Detection.NormalizedPayee).ToHashSet(StringComparer.Ordinal);
            var settings = factory.CreateDbContext();
            await using (settings.ConfigureAwait(false))
            {
                if (consumed.Count > 0)
                {
                    await DataFileSettings.StageAsync(settings, DataFileSettings.ReenabledPayees, reenabled.Where(p => !consumed.Contains(p)).ToList(), ct).ConfigureAwait(false);
                }

                await DataFileSettings.StageAsync<DateOnly?>(settings, DataFileSettings.LastDetection, asOf, ct).ConfigureAwait(false);
                await settings.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            var created = await alerts.EvaluateCoreAsync(asOf, batchIds, decisions, itemsBefore, ct).ConfigureAwait(false);
            bus.Publish(new RecurringChanged(toApply.Select(d => d.ItemId!.Value).ToList()));
            return new RecurringDetectionSummary(
                decisions.Count(d => d.Kind == RecurringDecisionKind.Create),
                decisions.Count(d => d.Kind == RecurringDecisionKind.Update && d.After!.Status != RecurringStatus.Ended),
                decisions.Count(d => d.Kind == RecurringDecisionKind.Update && d.After!.Status == RecurringStatus.Ended),
                decisions.Count(d => d.Reason == RecurringDecisionReason.Unchanged),
                decisions.Count(d => d.Reason == RecurringDecisionReason.Dismissed),
                created.Count);
        }
        finally
        {
            _detectGate.Release();
        }
    }

    private static async Task<List<RecurringItemDto>> MapAsync(KeelDbContext db, IReadOnlyList<RecurringItem> items, CancellationToken ct)
    {
        var currency = await M5Lookups.CurrencyAsync(db, ct).ConfigureAwait(false);
        var payees = await M5Lookups.PayeeNamesAsync(db, items.Select(i => i.PayeeId), ct).ConfigureAwait(false);
        var accountIds = items.Select(i => i.AccountId).OfType<Guid>().Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct).ConfigureAwait(false);
        var categoryIds = items.Select(i => i.CategoryId).OfType<Guid>().Distinct().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct).ConfigureAwait(false);
        return items.Select(i => new RecurringItemDto(
            i.Id,
            i.PayeeId,
            payees.GetValueOrDefault(i.PayeeId, string.Empty),
            i.AccountId,
            i.AccountId is { } a ? accounts.GetValueOrDefault(a) : null,
            i.CategoryId,
            i.CategoryId is { } c ? categories.GetValueOrDefault(c) : null,
            i.Cadence,
            Describe(i),
            new Money(i.ExpectedAmount, currency),
            new Money(i.AmountTolerance, currency),
            i.IsVariableAmount,
            new Money(RecurringMath.MonthlyEquivalent(i.ExpectedAmount, i.Cadence), currency),
            i.NextExpectedDate,
            i.LastSeenDate,
            i.Confidence,
            i.Status,
            i.IsSubscription,
            i.ScheduledTransactionId)).ToList();
    }

    private static string Describe(RecurringItem item)
    {
        try
        {
            return Rule(item).Describe(item.NextExpectedDate);
        }
        catch (ArgumentException)
        {
            return item.Cadence.ToString();
        }
    }

    // Past occurrences of each item in the range: its payee (by normalized name), its account (any when
    // the item has none), same direction as its expected amount; oldest first.
    private static async Task<Dictionary<Guid, List<RecurringOccurrenceDto>>> HistoryAsync(
        KeelDbContext db,
        IReadOnlyList<RecurringItem> items,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, List<RecurringOccurrenceDto>>();
        if (items.Count == 0 || to < from)
        {
            return result;
        }

        var currency = await M5Lookups.CurrencyAsync(db, ct).ConfigureAwait(false);
        var itemPayees = await RecurringInputs.NormalizedPayeesAsync(db, items.Select(i => i.PayeeId), ct).ConfigureAwait(false);
        var wanted = itemPayees.Values.ToHashSet(StringComparer.Ordinal);
        var payeeIds = await RecurringInputs.PayeesNamedAsync(db, wanted, ct).ConfigureAwait(false);
        var rows = (await RecurringInputs.LoadAsync(db, from, to, null, ct, payeeIds).ConfigureAwait(false))
            .Where(r => wanted.Contains(r.Transaction.NormalizedPayee))
            .OrderBy(r => r.Transaction.Date).ThenBy(r => r.Transaction.Id)
            .ToList();
        foreach (var item in items)
        {
            var payee = itemPayees.GetValueOrDefault(item.PayeeId);
            var outflow = item.ExpectedAmount < 0;
            result[item.Id] = rows
                .Where(r => string.Equals(r.Transaction.NormalizedPayee, payee, StringComparison.Ordinal)
                    && (item.AccountId is null || r.Transaction.AccountId == item.AccountId)
                    && r.Transaction.Amount != 0
                    && (r.Transaction.Amount < 0) == outflow)
                .Select(r => new RecurringOccurrenceDto(item.Id, r.Transaction.Date, new Money(r.Transaction.Amount, currency), r.Transaction.Id, false))
                .ToList();
        }

        return result;
    }
}
