using Keel.Application.Accounts;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Ledger;

/// <summary>Balance snapshots for tracking accounts and provider-reported balances (F-ACC-7).</summary>
public sealed class BalanceSnapshotService(IDbContextFactory<KeelDbContext> factory, LedgerWriter writer) : IBalanceSnapshotService
{
    /// <inheritdoc />
    public Task<BalanceSnapshotDto> RecordAsync(Guid accountId, DateOnly date, long balance, BalanceSource source, CancellationToken ct) =>
        writer.RunAsync(
            LedgerAction.RecordBalance,
            async session =>
            {
                var db = session.Db;
                await LedgerLookups.AccountAsync(db, accountId, ct).ConfigureAwait(false);
                var snapshot = await db.BalanceSnapshots.SingleOrDefaultAsync(s => s.AccountId == accountId && s.Date == date, ct).ConfigureAwait(false);
                if (snapshot is null)
                {
                    snapshot = new BalanceSnapshot { AccountId = accountId, Date = date };
                    db.BalanceSnapshots.Add(snapshot);
                }

                snapshot.Balance = balance;
                snapshot.Source = source;
                return ToDto(snapshot);
            },
            ct);

    /// <inheritdoc />
    public Task DeleteAsync(Guid accountId, DateOnly date, CancellationToken ct) =>
        writer.RunAsync(
            LedgerAction.DeleteBalance,
            async session =>
            {
                var snapshot = await session.Db.BalanceSnapshots.SingleOrDefaultAsync(s => s.AccountId == accountId && s.Date == date, ct).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    session.Db.BalanceSnapshots.Remove(snapshot);
                }

                return snapshot is not null;
            },
            ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<BalanceSnapshotDto>> GetSnapshotsAsync(Guid accountId, CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                IReadOnlyList<BalanceSnapshotDto> result = await db.BalanceSnapshots.AsNoTracking()
                    .Where(s => s.AccountId == accountId)
                    .OrderByDescending(s => s.Date)
                    .Select(s => new BalanceSnapshotDto(s.AccountId, s.Date, s.Balance, s.Source))
                    .ToListAsync(ct).ConfigureAwait(false);
                return result;
            }
        },
        ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, BalanceSnapshotDto>> GetLatestOnOrBeforeAsync(IReadOnlyCollection<Guid> accountIds, DateOnly date, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        return Task.Run(
            async () =>
            {
                var db = factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    var result = new Dictionary<Guid, BalanceSnapshotDto>();
                    foreach (var accountId in accountIds.Distinct())
                    {
                        // One indexed seek per account on the (AccountId, Date) primary key.
                        var latest = await db.BalanceSnapshots.AsNoTracking()
                            .Where(s => s.AccountId == accountId && s.Date <= date)
                            .OrderByDescending(s => s.Date)
                            .Select(s => new BalanceSnapshotDto(s.AccountId, s.Date, s.Balance, s.Source))
                            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                        if (latest is not null)
                        {
                            result[accountId] = latest;
                        }
                    }

                    return (IReadOnlyDictionary<Guid, BalanceSnapshotDto>)result;
                }
            },
            ct);
    }

    private static BalanceSnapshotDto ToDto(BalanceSnapshot s) => new(s.AccountId, s.Date, s.Balance, s.Source);
}
