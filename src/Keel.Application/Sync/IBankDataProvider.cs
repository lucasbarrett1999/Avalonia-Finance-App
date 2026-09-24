using Keel.Domain;

namespace Keel.Application.Sync;

/// <summary>A bank data provider (PRD 7.5). Plaid (M7) and SimpleFIN (P1) implement it.</summary>
public interface IBankDataProvider
{
    /// <summary>Provider id: "plaid" or "simplefin".</summary>
    string ProviderId { get; }

    /// <summary>Starts a link (or update-mode relink) session, e.g. a Plaid Hosted Link URL.</summary>
    Task<LinkSession> BeginLinkAsync(LinkMode mode, string? existingConnectionId, CancellationToken ct);

    /// <summary>Polls or receives the callback for a link session and exchanges the token.</summary>
    Task<LinkResult> CompleteLinkAsync(LinkSession session, CancellationToken ct);

    /// <summary>Lists the accounts of a connection.</summary>
    Task<IReadOnlyList<ProviderAccount>> ListAccountsAsync(string connectionId, CancellationToken ct);

    /// <summary>Fetches one page of transaction changes after <paramref name="cursor"/>.</summary>
    Task<SyncResult> SyncTransactionsAsync(string connectionId, string? cursor, CancellationToken ct);

    /// <summary>Fetches provider-reported balances.</summary>
    Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(string connectionId, CancellationToken ct);

    /// <summary>Checks connection health (e.g. ITEM_LOGIN_REQUIRED maps to NeedsReauth).</summary>
    Task<ConnectionHealth> GetHealthAsync(string connectionId, CancellationToken ct);

    /// <summary>Removes the connection at the provider; local transactions are kept.</summary>
    Task UnlinkAsync(string connectionId, CancellationToken ct);
}

/// <summary>Link mode.</summary>
public enum LinkMode
{
    /// <summary>Link a new institution.</summary>
    New,

    /// <summary>Repair an existing connection (update mode).</summary>
    Update,
}

/// <summary>An in-progress link session.</summary>
/// <param name="ProviderId">Provider.</param>
/// <param name="SessionToken">Provider session token (e.g. Plaid link token); never logged.</param>
/// <param name="LinkUrl">URL to open in the system browser.</param>
/// <param name="ExpiresAt">When the session expires.</param>
/// <param name="ExistingConnectionId">Connection being repaired, for update mode.</param>
public sealed record LinkSession(string ProviderId, string SessionToken, Uri LinkUrl, DateTimeOffset ExpiresAt, string? ExistingConnectionId)
{
    /// <summary>Whether the user finishes the link in the system browser (Plaid Hosted Link). SimpleFIN
    /// links from a setup token the user pasted, so there is nothing to open.</summary>
    public bool OpensBrowser { get; init; } = true;
}

/// <summary>Outcome of a link session.</summary>
public sealed record LinkResult(bool Succeeded, string? ConnectionId, string? InstitutionName, string? Error)
{
    /// <summary>The provider's id for the linked item (Plaid <c>item_id</c>), stored as <c>SyncConnection.ExternalItemId</c>.</summary>
    public string? ExternalItemId { get; init; }
}

/// <summary>An account at the provider.</summary>
public sealed record ProviderAccount(string ProviderAccountId, string Name, string? Mask, AccountType SuggestedType, string Currency);

/// <summary>A transaction at the provider, in Keel's sign convention (outflows negative).</summary>
public sealed record ProviderTransaction(
    string ProviderTransactionId,
    string ProviderAccountId,
    DateOnly Date,
    long Amount,
    string PayeeRaw,
    string? Memo,
    bool IsPending,
    string? PendingTransactionId);

/// <summary>One page of changes from <see cref="IBankDataProvider.SyncTransactionsAsync"/>.</summary>
/// <param name="Added">New transactions.</param>
/// <param name="Modified">Changed transactions.</param>
/// <param name="Removed">Provider ids of removed transactions.</param>
/// <param name="NextCursor">Cursor to store once the batch commits.</param>
/// <param name="HasMore">Whether another page is available.</param>
public sealed record SyncResult(
    IReadOnlyList<ProviderTransaction> Added,
    IReadOnlyList<ProviderTransaction> Modified,
    IReadOnlyList<string> Removed,
    string NextCursor,
    bool HasMore)
{
    /// <summary>Whether the provider has finished pulling the account history (Plaid
    /// <c>HISTORICAL_UPDATE_COMPLETE</c>); new linked accounts get their starting balance only then.</summary>
    public bool IsHistoryComplete { get; init; } = true;
}

/// <summary>A provider-reported balance in minor units.</summary>
public sealed record ProviderBalance(string ProviderAccountId, long Current, long? Available, string Currency, DateTimeOffset AsOf);

/// <summary>Connection health.</summary>
public sealed record ConnectionHealth(SyncStatus Status, string? Message, DateTimeOffset CheckedAt);
