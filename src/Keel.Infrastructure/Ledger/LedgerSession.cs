using Keel.Domain.Entities;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Ledger;

/// <summary>
/// One user action's database work. <see cref="SaveAsync"/> replaces <c>SaveChangesAsync</c>: it
/// stamps timestamps, captures every row change with its before/after state, and writes the
/// matching <see cref="AuditEvent"/> rows in the same database transaction.
/// </summary>
internal sealed class LedgerSession(KeelDbContext db, TimeProvider time)
{
    private readonly List<EntityChange> _changes = [];

    /// <summary>The context of this action.</summary>
    public KeelDbContext Db { get; } = db;

    /// <summary>Current UTC time.</summary>
    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    /// <summary>All captured changes so far, coalesced per row.</summary>
    public IReadOnlyList<EntityChange> Changes => EntityChange.Coalesce(_changes);

    /// <summary>Captures pending changes, adds audit rows, and saves.</summary>
    public async Task SaveAsync(CancellationToken ct)
    {
        Db.ChangeTracker.DetectChanges();
        var now = UtcNow;
        var captured = new List<EntityChange>();
        foreach (var entry in Db.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is AuditEvent || entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (entry.Entity is Transaction transaction)
            {
                if (entry.State == EntityState.Added)
                {
                    transaction.CreatedAt = transaction.CreatedAt == default ? now : transaction.CreatedAt;
                    transaction.UpdatedAt = now;
                }
                else if (entry.State == EntityState.Modified && entry.Properties.Any(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)))
                {
                    transaction.UpdatedAt = now;
                }
            }

            if (EntityChange.Capture(entry) is { } change)
            {
                captured.Add(change);
            }
        }

        foreach (var change in captured)
        {
            Db.AuditEvents.Add(change.ToAuditEvent(now));
        }

        await Db.SaveChangesAsync(ct).ConfigureAwait(false);
        _changes.AddRange(captured);
    }

    /// <summary>
    /// Adds changes written outside the change tracker in this transaction (the import's bulk
    /// insert, ADR 0052), already audited, so they join the undo entry and the change message.
    /// </summary>
    public void AddWrittenChanges(IEnumerable<EntityChange> changes) => _changes.AddRange(changes);
}
