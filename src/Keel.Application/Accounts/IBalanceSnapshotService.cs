using Keel.Domain;

namespace Keel.Application.Accounts;

/// <summary>
/// Dated balances for tracking accounts and provider-reported balances (F-ACC-7). Net worth uses
/// the latest snapshot on or before each point.
/// </summary>
public interface IBalanceSnapshotService
{
    /// <summary>Records (or replaces) the balance of an account on a date.</summary>
    Task<BalanceSnapshotDto> RecordAsync(Guid accountId, DateOnly date, long balance, BalanceSource source, CancellationToken ct);

    /// <summary>Deletes the snapshot of an account on a date; no-op when absent.</summary>
    Task DeleteAsync(Guid accountId, DateOnly date, CancellationToken ct);

    /// <summary>Snapshots of an account, newest first.</summary>
    Task<IReadOnlyList<BalanceSnapshotDto>> GetSnapshotsAsync(Guid accountId, CancellationToken ct);

    /// <summary>The latest snapshot on or before <paramref name="date"/> for each account that has one.</summary>
    Task<IReadOnlyDictionary<Guid, BalanceSnapshotDto>> GetLatestOnOrBeforeAsync(IReadOnlyCollection<Guid> accountIds, DateOnly date, CancellationToken ct);
}

/// <summary>A balance snapshot in minor units.</summary>
public sealed record BalanceSnapshotDto(Guid AccountId, DateOnly Date, long Balance, BalanceSource Source);
