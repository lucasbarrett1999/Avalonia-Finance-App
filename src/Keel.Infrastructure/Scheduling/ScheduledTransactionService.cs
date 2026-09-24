using Keel.Application.Ledger;
using Keel.Application.Recurring;
using Keel.Application.Scheduling;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Scheduling;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Recurring;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Scheduling;

/// <summary>
/// Scheduled transactions (F-ACC-6). Rules are stored explicit, without COUNT/UNTIL (the end date
/// holds them), and anchored at the next date (ADR 0035). Entering an instance creates the
/// transaction through the ledger's save path (transfers included) with <c>Source = Scheduled</c>
/// and <c>ScheduledFromId</c>, and advances the next date, in one undoable unit of work. Only the
/// next instance of a schedule can be entered or skipped, so instances are always taken in order.
/// </summary>
public sealed class ScheduledTransactionService(
    IDbContextFactory<KeelDbContext> factory,
    LedgerWriter writer,
    Application.Messaging.IMessageBus bus,
    TimeProvider time) : IScheduledTransactionService
{
    /// <summary>How many instances of one schedule <see cref="EnterDueAsync"/> enters at most (a years-old daily rule stays bounded).</summary>
    public const int MaxDueInstances = 400;

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduledTransactionDto>> GetAsync(Guid? accountId, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var rows = await db.ScheduledTransactions.AsNoTracking()
                    .Where(s => accountId == null || s.AccountId == accountId || s.TransferAccountId == accountId)
                    .ToListAsync(ct).ConfigureAwait(false);
                return (IReadOnlyList<ScheduledTransactionDto>)(await MapAsync(db, rows, ct).ConfigureAwait(false))
                    .OrderBy(d => d.NextDate).ThenBy(d => d.PayeeName, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
        },
        ct);

    /// <inheritdoc />
    public Task<ScheduledTransactionDto?> GetByIdAsync(Guid id, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var row = await db.ScheduledTransactions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
                return row is null ? null : (await MapAsync(db, [row], ct).ConfigureAwait(false))[0];
            }
        },
        ct);

    /// <inheritdoc />
    public async Task<ScheduledTransactionDto> CreateAsync(ScheduledTransactionEdit edit, CancellationToken ct)
    {
        var schedule = Validate(edit);
        var id = await writer.RunAsync(
            LedgerAction.CreateScheduled,
            async session =>
            {
                var entity = new ScheduledTransaction();
                await ApplyAsync(session.Db, entity, edit, schedule, ct).ConfigureAwait(false);
                session.Db.ScheduledTransactions.Add(entity);
                if (edit.RecurringItemId is { } itemId)
                {
                    var item = await session.Db.RecurringItems.SingleOrDefaultAsync(i => i.Id == itemId, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The recurring item does not exist.");
                    item.ScheduledTransactionId = entity.Id;
                }

                return entity.Id;
            },
            ct).ConfigureAwait(false);
        if (edit.RecurringItemId is { } linked)
        {
            bus.Publish(new RecurringChanged([linked]));
        }

        return (await GetByIdAsync(id, ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<ScheduledTransactionDto> UpdateAsync(Guid id, ScheduledTransactionEdit edit, CancellationToken ct)
    {
        var schedule = Validate(edit);
        await writer.RunAsync(
            LedgerAction.EditScheduled,
            async session =>
            {
                var entity = await LoadAsync(session.Db, id, ct).ConfigureAwait(false);
                await ApplyAsync(session.Db, entity, edit, schedule, ct).ConfigureAwait(false);
                return true;
            },
            ct).ConfigureAwait(false);
        return (await GetByIdAsync(id, ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var items = await writer.RunAsync(
            LedgerAction.DeleteScheduled,
            async session =>
            {
                var db = session.Db;
                var entity = await LoadAsync(db, id, ct).ConfigureAwait(false);

                // Clear the references in tracked rows so the change is audited and undo restores them.
                var entered = await db.Transactions.IgnoreQueryFilters().Where(t => t.ScheduledFromId == id).ToListAsync(ct).ConfigureAwait(false);
                entered.ForEach(t => t.ScheduledFromId = null);
                var linked = await db.RecurringItems.Where(i => i.ScheduledTransactionId == id).ToListAsync(ct).ConfigureAwait(false);
                linked.ForEach(i => i.ScheduledTransactionId = null);
                db.ScheduledTransactions.Remove(entity);
                return linked.Select(i => i.Id).ToList();
            },
            ct).ConfigureAwait(false);
        if (items.Count > 0)
        {
            bus.Publish(new RecurringChanged(items));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduledInstanceDto>> GetUpcomingAsync(DateOnly from, DateOnly to, Guid? accountId, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var rows = await db.ScheduledTransactions.AsNoTracking()
                    .Where(s => accountId == null || s.AccountId == accountId || s.TransferAccountId == accountId)
                    .ToListAsync(ct).ConfigureAwait(false);
                var dtos = (await MapAsync(db, rows, ct).ConfigureAwait(false)).ToDictionary(d => d.Id);
                var today = M5Lookups.Today(time);
                var result = new List<ScheduledInstanceDto>();
                foreach (var row in rows)
                {
                    var dto = dtos[row.Id];
                    if (dto.IsFinished || !RecurrenceRule.TryParse(row.RecurrenceRule, out var rule, out _))
                    {
                        continue;
                    }

                    var last = row.EndDate is { } end && end < to ? end : to;
                    foreach (var date in rule.Occurrences(row.NextDate, from, last))
                    {
                        var isNext = date == row.NextDate;
                        if (accountId is null || row.AccountId == accountId)
                        {
                            result.Add(new ScheduledInstanceDto(row.Id, row.AccountId, date, dto.Amount, dto.PayeeName, date < today, isNext,
                                dto.TransferAccountName, dto.CategoryName, row.Memo, row.AutoEnter));
                        }

                        if (row.TransferAccountId is { } other && (accountId is null || other == accountId) && accountId is not null)
                        {
                            // The other side of a scheduled transfer, as it will appear in that register.
                            result.Add(new ScheduledInstanceDto(row.Id, other, date, new Money(-row.Amount, dto.Amount.Currency), dto.PayeeName, date < today, isNext,
                                dto.AccountName, dto.CategoryName, row.Memo, row.AutoEnter));
                        }
                    }
                }

                return (IReadOnlyList<ScheduledInstanceDto>)result.OrderBy(i => i.Date).ThenBy(i => i.PayeeName, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
        },
        ct);

    /// <inheritdoc />
    public Task<EnterDueResult> EnterDueAsync(DateOnly today, CancellationToken ct) => Task.Run(
        async () =>
        {
            List<ScheduledTransaction> due;
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                due = await db.ScheduledTransactions.AsNoTracking().Where(s => s.NextDate <= today).ToListAsync(ct).ConfigureAwait(false);
            }

            due = due.Where(s => !IsFinished(s)).ToList();
            var auto = due.Where(s => s.AutoEnter).Select(s => s.Id).ToList();
            IReadOnlyList<Guid> entered = [];
            if (auto.Count > 0)
            {
                entered = await writer.RunAsync(
                    LedgerAction.EnterScheduled,
                    async session =>
                    {
                        var ids = new List<Guid>();
                        foreach (var id in auto)
                        {
                            var entity = await LoadAsync(session.Db, id, ct).ConfigureAwait(false);
                            for (var i = 0; i < MaxDueInstances && !IsFinished(entity) && entity.NextDate <= today; i++)
                            {
                                ids.Add(await EnterCoreAsync(session, entity, entity.NextDate, ct).ConfigureAwait(false));
                            }
                        }

                        return ids;
                    },
                    ct).ConfigureAwait(false);
            }

            var prompts = due.Count == auto.Count
                ? []
                : (await GetUpcomingAsync(due.Min(s => s.NextDate), today, null, ct).ConfigureAwait(false))
                    .Where(i => !i.AutoEnter && due.Any(s => s.Id == i.ScheduledId && s.AccountId == i.AccountId))
                    .ToList();
            return new EnterDueResult(entered, prompts);
        },
        ct);

    /// <inheritdoc />
    public Task<Guid> EnterInstanceAsync(Guid scheduledId, DateOnly instanceDate, CancellationToken ct) =>
        writer.RunAsync(
            LedgerAction.EnterScheduled,
            async session =>
            {
                var entity = await LoadAsync(session.Db, scheduledId, ct).ConfigureAwait(false);
                EnsureNext(entity, instanceDate);
                return await EnterCoreAsync(session, entity, instanceDate, ct).ConfigureAwait(false);
            },
            ct);

    /// <inheritdoc />
    public Task SkipInstanceAsync(Guid scheduledId, DateOnly instanceDate, CancellationToken ct) =>
        writer.RunAsync(
            LedgerAction.SkipScheduled,
            async session =>
            {
                var entity = await LoadAsync(session.Db, scheduledId, ct).ConfigureAwait(false);
                EnsureNext(entity, instanceDate);
                Advance(entity);
                return true;
            },
            ct);

    /// <inheritdoc />
    public RecurrenceRuleCheck CheckRule(string rule, DateOnly start, int previewCount)
    {
        if (!RecurrenceRule.TryParse(rule, out _, out var error))
        {
            return new RecurrenceRuleCheck(false, error, null, null, []);
        }

        var stored = ScheduleRules.Normalize(rule, start, null);
        var dates = stored.First is { } first
            ? stored.Rule.Occurrences(first, first, stored.EndDate ?? DateOnly.MaxValue).Take(Math.Max(0, previewCount)).ToList()
            : [];
        return new RecurrenceRuleCheck(true, null, RecurrenceRule.Parse(rule).ToString(), RecurrenceRule.Parse(rule).Describe(start), dates);
    }

    /// <summary>True when the schedule has no instances left.</summary>
    internal static bool IsFinished(ScheduledTransaction s) => s.NextDate == DateOnly.MaxValue || (s.EndDate is { } end && s.NextDate > end);

    private static StoredSchedule Validate(ScheduledTransactionEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.Amount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(edit), "The amount must not be zero.");
        }

        if (edit.TransferAccountId == edit.AccountId)
        {
            throw new LedgerValidationException(LedgerError.TransferToSameAccount);
        }

        var schedule = ScheduleRules.Normalize(edit.Rule, edit.StartDate, edit.EndDate);
        return schedule.First is null
            ? throw new RecurrenceRuleFormatException("The rule has no instance on or after the start date.")
            : schedule;
    }

    private static async Task ApplyAsync(KeelDbContext db, ScheduledTransaction entity, ScheduledTransactionEdit edit, StoredSchedule schedule, CancellationToken ct)
    {
        await LedgerLookups.OpenAccountAsync(db, edit.AccountId, ct).ConfigureAwait(false);
        if (edit.TransferAccountId is { } other)
        {
            await LedgerLookups.OpenAccountAsync(db, other, ct).ConfigureAwait(false);
        }

        await LedgerLookups.EnsureCategoryAsync(db, edit.CategoryId, ct).ConfigureAwait(false);
        if (!await db.Payees.AnyAsync(p => p.Id == edit.PayeeId, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The payee does not exist.");
        }

        entity.AccountId = edit.AccountId;
        entity.PayeeId = edit.PayeeId;
        entity.Amount = edit.Amount;
        entity.CategoryId = edit.CategoryId;
        entity.TransferAccountId = edit.TransferAccountId;
        entity.Memo = string.IsNullOrWhiteSpace(edit.Memo) ? null : edit.Memo.Trim();
        entity.RecurrenceRule = schedule.Rule.ToString();
        entity.NextDate = schedule.First!.Value;
        entity.EndDate = schedule.EndDate;
        entity.AutoEnter = edit.AutoEnter;
    }

    private static async Task<ScheduledTransaction> LoadAsync(KeelDbContext db, Guid id, CancellationToken ct) =>
        await db.ScheduledTransactions.SingleOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The scheduled transaction does not exist.");

    private static void EnsureNext(ScheduledTransaction entity, DateOnly instanceDate)
    {
        if (IsFinished(entity) || entity.NextDate != instanceDate)
        {
            throw new InvalidOperationException("Only the next instance of a schedule can be entered or skipped.");
        }
    }

    private static void Advance(ScheduledTransaction entity)
    {
        var rule = RecurrenceRule.Parse(entity.RecurrenceRule);
        entity.NextDate = rule.NextAfter(entity.NextDate, entity.NextDate) ?? DateOnly.MaxValue;
    }

    // Creates the instance's transaction through the ledger save path and advances the schedule.
    private static async Task<Guid> EnterCoreAsync(LedgerSession session, ScheduledTransaction entity, DateOnly date, CancellationToken ct)
    {
        var payee = await session.Db.Payees.AsNoTracking().Where(p => p.Id == entity.PayeeId).Select(p => p.Name).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var request = new SaveTransactionRequest(
            null,
            entity.AccountId,
            date,
            entity.Amount,
            payee,
            entity.CategoryId,
            entity.Memo,
            TransactionStatus.Uncleared,
            IsApproved: true,
            TransferAccountId: entity.TransferAccountId);
        var id = await TransactionService.SaveCoreAsync(session, request, [], ct).ConfigureAwait(false);
        var txn = session.Db.Transactions.Local.Single(t => t.Id == id);
        txn.Source = TransactionSource.Scheduled;
        txn.ScheduledFromId = entity.Id;
        if (txn.TransferPairId is { } pairId)
        {
            foreach (var pair in session.Db.Transactions.Local.Where(t => t.TransferPairId == pairId && t.Id != id))
            {
                pair.Source = TransactionSource.Scheduled;
                pair.ScheduledFromId = entity.Id;
            }
        }

        Advance(entity);
        return id;
    }

    private static async Task<List<ScheduledTransactionDto>> MapAsync(KeelDbContext db, IReadOnlyList<ScheduledTransaction> rows, CancellationToken ct)
    {
        var currency = await M5Lookups.CurrencyAsync(db, ct).ConfigureAwait(false);
        var payees = await M5Lookups.PayeeNamesAsync(db, rows.Select(r => r.PayeeId), ct).ConfigureAwait(false);
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name, ct).ConfigureAwait(false);
        var categoryIds = rows.Select(r => r.CategoryId).OfType<Guid>().Distinct().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct).ConfigureAwait(false);
        return rows.Select(r => new ScheduledTransactionDto(
            r.Id,
            r.AccountId,
            r.PayeeId,
            payees.GetValueOrDefault(r.PayeeId, string.Empty),
            new Money(r.Amount, currency),
            r.CategoryId,
            r.TransferAccountId,
            r.Memo,
            r.RecurrenceRule,
            Describe(r),
            r.NextDate,
            r.EndDate,
            r.AutoEnter,
            accounts.GetValueOrDefault(r.AccountId),
            r.CategoryId is { } c ? categories.GetValueOrDefault(c) : null,
            r.TransferAccountId is { } t ? accounts.GetValueOrDefault(t) : null,
            IsFinished(r))).ToList();
    }

    private static string Describe(ScheduledTransaction row)
    {
        if (!RecurrenceRule.TryParse(row.RecurrenceRule, out var rule, out _))
        {
            return row.RecurrenceRule;
        }

        var text = rule.Describe(row.NextDate);
        return row.EndDate is { } end
            ? rule.WithUntil(end).Describe(row.NextDate)
            : text;
    }
}
