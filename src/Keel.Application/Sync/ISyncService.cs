using Keel.Domain;

namespace Keel.Application.Sync;

/// <summary>
/// Bank sync use cases (F-TXN-3, F-SET-3): link an institution, map its accounts, sync one or all
/// connections through the unified import pipeline, reconnect, and unlink. Every call runs off the
/// UI thread; imported rows publish <c>LedgerChanged</c> like any other import, and connection
/// state changes publish <see cref="SyncConnectionsChanged"/>.
/// </summary>
public interface ISyncService
{
    /// <summary>The registered providers (Plaid, SimpleFIN).</summary>
    IReadOnlyList<BankProviderInfo> Providers { get; }

    /// <summary>Whether a sync is running.</summary>
    bool IsSyncing { get; }

    /// <summary>Connections with their linked accounts, by institution name.</summary>
    Task<IReadOnlyList<SyncConnectionDto>> GetConnectionsAsync(CancellationToken ct);

    /// <summary>Starts linking a new institution: returns the session whose URL the user opens.</summary>
    Task<LinkSession> BeginLinkAsync(string providerId, CancellationToken ct);

    /// <summary>
    /// Waits for the user to finish the link (Plaid polls <c>/link/token/get</c>), exchanges the
    /// token, and lists the institution's accounts with a suggested mapping. Nothing is written to
    /// the budget file until <see cref="SaveLinkAsync"/>; <see cref="DiscardLinkAsync"/> undoes the link.
    /// </summary>
    Task<PendingConnection> CompleteLinkAsync(LinkSession session, CancellationToken ct);

    /// <summary>Creates the connection and creates or links the chosen accounts (one undoable action per account change).</summary>
    Task<SyncConnectionDto> SaveLinkAsync(PendingConnection pending, IReadOnlyList<AccountLinkChoice> choices, CancellationToken ct);

    /// <summary>Removes a link the user did not save (provider item and stored token).</summary>
    Task DiscardLinkAsync(PendingConnection pending, CancellationToken ct);

    /// <summary>Starts repairing a connection that needs the user to sign in again (Plaid update mode).</summary>
    Task<LinkSession> BeginReconnectAsync(Guid connectionId, CancellationToken ct);

    /// <summary>Waits for the reconnect to finish and marks the connection healthy.</summary>
    Task<SyncConnectionDto> CompleteReconnectAsync(LinkSession session, CancellationToken ct);

    /// <summary>
    /// Syncs one connection: pages of added, modified and removed transactions until the provider
    /// has no more, each page imported through the pipeline (source Provider) before its cursor is
    /// stored; then balances as reported balances and snapshots. Failures are recorded as the
    /// connection's health and returned, not thrown.
    /// </summary>
    Task<SyncRunResult> SyncConnectionAsync(Guid connectionId, IProgress<SyncProgress>? progress, CancellationToken ct);

    /// <summary>Syncs the connection an account is linked to; null when the account is not linked.</summary>
    Task<SyncRunResult?> SyncAccountAsync(Guid accountId, IProgress<SyncProgress>? progress, CancellationToken ct);

    /// <summary>Syncs every enabled connection, one after another.</summary>
    Task<SyncAllResult> SyncAllAsync(IProgress<SyncProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Removes the item at the provider, deletes the stored token, unlinks the accounts, and deletes
    /// the connection. Local transactions are kept.
    /// </summary>
    Task UnlinkAsync(Guid connectionId, CancellationToken ct);

    /// <summary>Sync preferences kept in the budget file.</summary>
    Task<SyncSettings> GetSettingsAsync(CancellationToken ct);

    /// <summary>Saves sync preferences.</summary>
    Task SaveSettingsAsync(SyncSettings settings, CancellationToken ct);
}

/// <summary>A bank data provider as shown in the add-connection dialog.</summary>
/// <param name="ProviderId">"plaid" or "simplefin".</param>
/// <param name="Kind">Stored provider kind.</param>
public sealed record BankProviderInfo(string ProviderId, SyncProvider Kind);

/// <summary>A sync connection with its linked accounts (Settings → Connections).</summary>
/// <param name="Id">Connection id.</param>
/// <param name="Provider">Provider.</param>
/// <param name="InstitutionName">Institution.</param>
/// <param name="Status">Health.</param>
/// <param name="LastSyncAt">Last successful sync (UTC).</param>
/// <param name="LastError">Error code or message of the last failure (never a secret).</param>
/// <param name="Accounts">Linked Keel accounts.</param>
public sealed record SyncConnectionDto(
    Guid Id,
    SyncProvider Provider,
    string InstitutionName,
    SyncStatus Status,
    DateTime? LastSyncAt,
    string? LastError,
    IReadOnlyList<LinkedAccountDto> Accounts);

/// <summary>A Keel account linked to a provider account.</summary>
/// <param name="AccountId">Keel account.</param>
/// <param name="Name">Keel account name.</param>
/// <param name="ProviderAccountId">Provider account id.</param>
/// <param name="ReportedBalance">Latest provider balance (minor units), if any.</param>
/// <param name="Currency">Account currency.</param>
public sealed record LinkedAccountDto(Guid AccountId, string Name, string ProviderAccountId, long? ReportedBalance, string Currency);

/// <summary>A finished link waiting for the user to map its accounts.</summary>
/// <param name="ProviderId">Provider.</param>
/// <param name="ConnectionId">Id the connection will get (its secret is stored under it).</param>
/// <param name="InstitutionName">Institution.</param>
/// <param name="ExternalItemId">Provider item id.</param>
/// <param name="Accounts">Provider accounts with suggestions.</param>
public sealed record PendingConnection(
    string ProviderId,
    Guid ConnectionId,
    string InstitutionName,
    string ExternalItemId,
    IReadOnlyList<PendingAccount> Accounts);

/// <summary>One provider account in the mapping dialog.</summary>
/// <param name="Account">The provider account.</param>
/// <param name="Balance">Its reported balance, when known.</param>
/// <param name="SuggestedExistingAccountId">An unlinked Keel account with the same type and a matching name or mask, if any.</param>
public sealed record PendingAccount(ProviderAccount Account, ProviderBalance? Balance, Guid? SuggestedExistingAccountId);

/// <summary>What to do with one provider account.</summary>
public enum AccountLinkAction
{
    /// <summary>Create a new Keel account for it.</summary>
    CreateNew,

    /// <summary>Link it to an existing Keel account.</summary>
    LinkExisting,

    /// <summary>Do not import it.</summary>
    Skip,
}

/// <summary>The user's mapping choice for one provider account.</summary>
/// <param name="ProviderAccountId">Provider account.</param>
/// <param name="Action">Create, link, or skip.</param>
/// <param name="ExistingAccountId">For <see cref="AccountLinkAction.LinkExisting"/>.</param>
/// <param name="NewName">For <see cref="AccountLinkAction.CreateNew"/>.</param>
/// <param name="NewType">For <see cref="AccountLinkAction.CreateNew"/>.</param>
public sealed record AccountLinkChoice(
    string ProviderAccountId,
    AccountLinkAction Action,
    Guid? ExistingAccountId = null,
    string? NewName = null,
    AccountType NewType = AccountType.Checking);

/// <summary>Progress of a running sync, for the status strip.</summary>
/// <param name="InstitutionName">Connection being synced.</param>
/// <param name="ConnectionIndex">1-based index of the connection in a sync-all run.</param>
/// <param name="ConnectionCount">Connections in the run.</param>
/// <param name="TransactionsReceived">Rows received so far for this connection.</param>
public sealed record SyncProgress(string InstitutionName, int ConnectionIndex, int ConnectionCount, int TransactionsReceived);

/// <summary>Outcome of syncing one connection.</summary>
/// <param name="ConnectionId">Connection.</param>
/// <param name="InstitutionName">Institution.</param>
/// <param name="Status">Health after the run.</param>
/// <param name="Added">Rows inserted.</param>
/// <param name="Updated">Rows updated in place (modified, pending to posted, matched).</param>
/// <param name="Removed">Rows the provider removed (soft-deleted here).</param>
/// <param name="ErrorCode">Failure code (<see cref="BankProviderException.Code"/>), when the run failed.</param>
public sealed record SyncRunResult(
    Guid ConnectionId,
    string InstitutionName,
    SyncStatus Status,
    int Added,
    int Updated,
    int Removed,
    string? ErrorCode)
{
    /// <summary>Whether the run finished without error.</summary>
    public bool Succeeded => ErrorCode is null;
}

/// <summary>Outcome of a sync-all run.</summary>
/// <param name="Connections">One result per connection.</param>
public sealed record SyncAllResult(IReadOnlyList<SyncRunResult> Connections)
{
    /// <summary>Rows inserted over all connections.</summary>
    public int Added => Connections.Sum(c => c.Added);

    /// <summary>Rows updated over all connections.</summary>
    public int Updated => Connections.Sum(c => c.Updated);

    /// <summary>Rows removed over all connections.</summary>
    public int Removed => Connections.Sum(c => c.Removed);

    /// <summary>Connections that failed.</summary>
    public int Failed => Connections.Count(c => !c.Succeeded);
}

/// <summary>Sync preferences stored in the budget file.</summary>
/// <param name="IntervalHours">Hours between scheduled syncs while the app runs; 0 turns scheduled sync off.</param>
/// <param name="SyncOnStart">Whether to sync all connections when the app opens the file.</param>
public sealed record SyncSettings(int IntervalHours = SyncSettings.DefaultIntervalHours, bool SyncOnStart = true)
{
    /// <summary>Default interval.</summary>
    public const int DefaultIntervalHours = 4;

    /// <summary>Intervals offered in Settings.</summary>
    public static IReadOnlyList<int> IntervalChoices { get; } = [0, 1, 2, 4, 8, 12, 24];
}

/// <summary>Connections, their health or their accounts changed.</summary>
/// <param name="ConnectionIds">Connections that changed (empty when unknown).</param>
public sealed record SyncConnectionsChanged(IReadOnlyCollection<Guid> ConnectionIds);
