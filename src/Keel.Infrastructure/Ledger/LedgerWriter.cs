using Keel.Application.Messaging;
using Keel.Application.Undo;
using Keel.Domain.Budgeting;
using Keel.Domain.Entities;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Ledger;

/// <summary>
/// Runs every ledger mutation as one unit of work on the thread pool: one short-lived context, one
/// database transaction, audit events for every changed row, an undo entry, and a
/// <see cref="LedgerChanged"/> message after the commit (plus <see cref="BudgetChanged"/> when
/// assignment or target rows changed, e.g. undoing a budget action; a replay that touches only
/// those rows publishes <see cref="BudgetChanged"/> alone). Writes are serialized so the undo
/// stack order is the commit order.
/// </summary>
public sealed class LedgerWriter(IDbContextFactory<KeelDbContext> factory, UndoHistory history, IMessageBus bus, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal enum Recording
    {
        /// <summary>A new user action: push to the undo stack and clear redo.</summary>
        NewAction,

        /// <summary>Undo or redo replay: the caller manages the stacks.</summary>
        None,
    }

    /// <summary>Runs <paramref name="work"/> as the user action <paramref name="action"/>.</summary>
    internal Task<T> RunAsync<T>(LedgerAction action, Func<LedgerSession, Task<T>> work, CancellationToken ct) =>
        Task.Run(() => RunCoreAsync(action, work, Recording.NewAction, ct), ct);

    /// <summary>Runs <paramref name="work"/>; returns the result and the coalesced changes.</summary>
    internal async Task<T> RunCoreAsync<T>(LedgerAction action, Func<LedgerSession, Task<T>> work, Recording recording, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        LedgerChanged? message;
        BudgetChanged? budgetMessage;
        T result;
        try
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var session = new LedgerSession(db, time);
                    result = await work(session).ConfigureAwait(false);
                    await session.SaveAsync(ct).ConfigureAwait(false);
                    var changes = session.Changes;
                    var ledgerRows = changes.Count(c => !IsBudgetRow(c.EntityType));
                    message = ledgerRows == 0 ? null : await DescribeAsync(db, changes, ct).ConfigureAwait(false);
                    budgetMessage = ledgerRows == changes.Count ? null : DescribeBudget(changes);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);

                    if (changes.Count > 0 && recording == Recording.NewAction)
                    {
                        history.Record(new UndoEntry(action, changes));
                    }

                    LastChanges = changes;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (message is not null)
        {
            bus.Publish(message);
        }

        if (budgetMessage is not null)
        {
            bus.Publish(budgetMessage);
        }

        return result;
    }

    /// <summary>Changes of the most recent unit of work (for undo bookkeeping and tests).</summary>
    internal IReadOnlyList<EntityChange> LastChanges { get; private set; } = [];

    private static bool IsBudgetRow(Type type) => type == typeof(BudgetAssignment) || type == typeof(Target);

    // Months whose budget changed: assignment months; target changes count for the current month
    // (as BudgetService publishes them).
    private BudgetChanged DescribeBudget(IReadOnlyList<EntityChange> changes)
    {
        var months = new HashSet<DateOnly>();
        foreach (var change in changes)
        {
            if (change.EntityType == typeof(BudgetAssignment))
            {
                months.UnionWith(change.Values(nameof(BudgetAssignment.Month)).OfType<DateOnly>().Select(BudgetMonth.Of));
            }
            else if (change.EntityType == typeof(Target))
            {
                months.Add(BudgetMonth.Of(DateOnly.FromDateTime(time.GetLocalNow().DateTime)));
            }
        }

        return new BudgetChanged(months);
    }

    // Which accounts and months a set of row changes touched.
    private static async Task<LedgerChanged> DescribeAsync(KeelDbContext db, IReadOnlyList<EntityChange> changes, CancellationToken ct)
    {
        var accounts = new HashSet<Guid>();
        var months = new HashSet<DateOnly>();
        var splitParents = new HashSet<Guid>();

        foreach (var change in changes)
        {
            if (change.EntityType == typeof(Transaction))
            {
                accounts.UnionWith(change.Values(nameof(Transaction.AccountId)).OfType<Guid>());
                months.UnionWith(change.Values(nameof(Transaction.Date)).OfType<DateOnly>().Select(BudgetAssignment.MonthOf));
            }
            else if (change.EntityType == typeof(TransactionSplit))
            {
                splitParents.UnionWith(change.Values(nameof(TransactionSplit.TransactionId)).OfType<Guid>());
            }
            else if (change.EntityType == typeof(Account))
            {
                accounts.UnionWith(change.Values(nameof(Account.Id)).OfType<Guid>());
            }
            else if (change.EntityType == typeof(BalanceSnapshot))
            {
                accounts.UnionWith(change.Values(nameof(BalanceSnapshot.AccountId)).OfType<Guid>());
                months.UnionWith(change.Values(nameof(BalanceSnapshot.Date)).OfType<DateOnly>().Select(BudgetAssignment.MonthOf));
            }
            else if (change.EntityType == typeof(Reconciliation))
            {
                accounts.UnionWith(change.Values(nameof(Reconciliation.AccountId)).OfType<Guid>());
            }
        }

        if (splitParents.Count > 0)
        {
            var parents = await db.Transactions.IgnoreQueryFilters().AsNoTracking()
                .Where(t => splitParents.Contains(t.Id))
                .Select(t => new { t.AccountId, t.Date })
                .ToListAsync(ct).ConfigureAwait(false);
            accounts.UnionWith(parents.Select(p => p.AccountId));
            months.UnionWith(parents.Select(p => BudgetAssignment.MonthOf(p.Date)));
        }

        return new LedgerChanged(accounts, months);
    }
}
