using Going.Plaid;
using Going.Plaid.Accounts;
using Going.Plaid.Entity;
using Going.Plaid.Item;
using Going.Plaid.Link;
using Going.Plaid.Transactions;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Domain;
using Microsoft.Extensions.Logging;
using EntityIds = Keel.Domain.Entities.EntityIds;

namespace Keel.Infrastructure.Sync.Plaid;

/// <summary>Tunables of the Plaid provider (tests shorten the polling).</summary>
public sealed record PlaidProviderOptions
{
    /// <summary>How often <c>/link/token/get</c> is polled while the user links (PRD 7.5: 2 s).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a link waits for the user (PRD 7.5: 10 min).</summary>
    public TimeSpan LinkTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Days of history requested when linking.</summary>
    public int DaysRequested { get; init; } = 90;

    /// <summary>Rows per <c>/transactions/sync</c> page (Plaid's maximum is 500).</summary>
    public int PageSize { get; init; } = 500;
}

/// <summary>
/// The Plaid provider (F-TXN-3, PRD 7.5): bring-your-own keys from the secret store; Hosted Link
/// opened in the system browser with <c>/link/token/get</c> polling (no WebView); access tokens in
/// the secret store under the connection's <c>SecretRef</c>; <c>/transactions/sync</c> with
/// cursors; update-mode links for <c>ITEM_LOGIN_REQUIRED</c>; <c>/item/remove</c> on unlink.
/// Tokens, secrets and payee text are never logged.
/// </summary>
public sealed partial class PlaidProvider(
    IPlaidApi api,
    ISecretStore secrets,
    TimeProvider time,
    ILogger<PlaidProvider> logger,
    PlaidProviderOptions? options = null) : IBankDataProvider
{
    /// <summary>The provider id.</summary>
    public const string Id = "plaid";

    /// <summary>The Plaid user id Keel links as: one local user per set of keys, no personal data.</summary>
    public const string ClientUserId = "keel-local-user";

    private readonly PlaidProviderOptions _options = options ?? new PlaidProviderOptions();

    /// <inheritdoc />
    public string ProviderId => Id;

    /// <inheritdoc />
    public async Task<LinkSession> BeginLinkAsync(LinkMode mode, string? existingConnectionId, CancellationToken ct)
    {
        var credentials = await PlaidCredentials.LoadAsync(secrets).ConfigureAwait(false);
        var environment = credentials.Environment;
        string? accessToken = null;
        if (mode == LinkMode.Update)
        {
            ArgumentException.ThrowIfNullOrEmpty(existingConnectionId);
            var connection = await ConnectionSecret.LoadAsync(secrets, existingConnectionId).ConfigureAwait(false);
            environment = PlaidCredentials.ParseEnvironment(connection.Environment) ?? environment;
            accessToken = connection.AccessToken;
        }

        // Update mode passes the item's access token and no products; a new link asks for transactions.
        var request = new LinkTokenCreateRequest
        {
            ClientId = credentials.ClientId,
            Secret = credentials.Secret,
            AccessToken = accessToken,
            ClientName = "Keel",
            Language = Language.English,
            CountryCodes = [CountryCode.Us, CountryCode.Ca],
            User = new LinkTokenCreateRequestUser { ClientUserId = ClientUserId },
            HostedLink = new LinkTokenCreateHostedLink(),
            Products = accessToken is null ? [Products.Transactions] : null,
            Transactions = accessToken is null ? new LinkTokenTransactions { DaysRequested = _options.DaysRequested } : null,
        };

        var response = await CallAsync(() => api.LinkTokenCreateAsync(environment, request, ct), "link/token/create").ConfigureAwait(false);
        if (string.IsNullOrEmpty(response.HostedLinkUrl) || !Uri.TryCreate(response.HostedLinkUrl, UriKind.Absolute, out var url))
        {
            throw new BankProviderException(SyncStatus.Error, BankErrorCodes.Unknown, "Plaid did not return a Hosted Link URL.");
        }

        LogLinkStarted(logger, mode, environment);
        return new LinkSession(Id, response.LinkToken, url, response.Expiration, mode == LinkMode.Update ? existingConnectionId : null);
    }

    /// <inheritdoc />
    public async Task<LinkResult> CompleteLinkAsync(LinkSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var credentials = await PlaidCredentials.LoadAsync(secrets).ConfigureAwait(false);
        var environment = credentials.Environment;
        ConnectionSecret? existing = null;
        if (session.ExistingConnectionId is { } existingId)
        {
            existing = await ConnectionSecret.LoadAsync(secrets, existingId).ConfigureAwait(false);
            environment = PlaidCredentials.ParseEnvironment(existing.Environment) ?? environment;
        }

        var deadline = time.GetUtcNow() + _options.LinkTimeout;
        if (session.ExpiresAt > DateTimeOffset.MinValue && session.ExpiresAt < deadline)
        {
            deadline = session.ExpiresAt;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var response = await CallAsync(
                () => api.LinkTokenGetAsync(environment, new LinkTokenGetRequest { ClientId = credentials.ClientId, Secret = credentials.Secret, LinkToken = session.SessionToken }, ct),
                "link/token/get").ConfigureAwait(false);
            var sessions = response.LinkSessions ?? [];

            if (existing is not null)
            {
                // Update mode returns no new public token: a finished session without an exit is the repair.
                if (sessions.FirstOrDefault(s => s.FinishedAt is not null && s.Exit is null) is { } done)
                {
                    LogLinkFinished(logger, true);
                    return new LinkResult(true, session.ExistingConnectionId, null, null) { ExternalItemId = existing.ItemId };
                }
            }
            else if (sessions.SelectMany(s => s.Results?.ItemAddResults ?? []).FirstOrDefault(r => !string.IsNullOrEmpty(r.PublicToken)) is { } added)
            {
                var exchange = await CallAsync(
                    () => api.ItemPublicTokenExchangeAsync(environment, new ItemPublicTokenExchangeRequest { ClientId = credentials.ClientId, Secret = credentials.Secret, PublicToken = added.PublicToken }, ct),
                    "item/public_token/exchange").ConfigureAwait(false);
                var connectionId = EntityIds.New().ToString();
                await new ConnectionSecret
                {
                    Provider = Id,
                    Environment = PlaidCredentials.EnvironmentName(environment),
                    AccessToken = exchange.AccessToken,
                    ItemId = exchange.ItemId,
                }.SaveAsync(secrets, connectionId).ConfigureAwait(false);
                LogLinkFinished(logger, true);
                return new LinkResult(true, connectionId, added.Institution?.Name, null) { ExternalItemId = exchange.ItemId };
            }

            if (sessions.Count > 0 && sessions.All(s => s.FinishedAt is not null && s.Exit is not null))
            {
                LogLinkFinished(logger, false);
                return new LinkResult(false, null, null, BankErrorCodes.LinkExited);
            }

            if (time.GetUtcNow() >= deadline)
            {
                LogLinkFinished(logger, false);
                return new LinkResult(false, null, null, BankErrorCodes.LinkExpired);
            }

            await Task.Delay(_options.PollInterval, time, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderAccount>> ListAccountsAsync(string connectionId, CancellationToken ct)
    {
        var accounts = await GetAccountsAsync(connectionId, ct).ConfigureAwait(false);
        return accounts.Select(PlaidMapping.ToProviderAccount).ToList();
    }

    /// <inheritdoc />
    public async Task<SyncResult> SyncTransactionsAsync(string connectionId, string? cursor, CancellationToken ct)
    {
        var (credentials, connection, environment) = await LoadAsync(connectionId).ConfigureAwait(false);
        var response = await CallAsync(
            () => api.TransactionsSyncAsync(
                environment,
                new TransactionsSyncRequest
                {
                    ClientId = credentials.ClientId,
                    Secret = credentials.Secret,
                    AccessToken = connection.AccessToken,
                    Cursor = string.IsNullOrEmpty(cursor) ? null : cursor,
                    Count = _options.PageSize,
                    Options = new TransactionsSyncRequestOptions { DaysRequested = _options.DaysRequested },
                },
                ct),
            "transactions/sync").ConfigureAwait(false);

        var currencies = (response.Accounts ?? [])
            .ToDictionary(a => a.AccountId, a => PlaidMapping.CurrencyOf(a.Balances?.IsoCurrencyCode, Currency.Default), StringComparer.Ordinal);
        var added = (response.Added ?? []).Select(t => PlaidMapping.ToProviderTransaction(t, currencies)).ToList();
        var modified = (response.Modified ?? []).Select(t => PlaidMapping.ToProviderTransaction(t, currencies)).ToList();
        var removed = (response.Removed ?? []).Select(r => r.TransactionId).Where(id => !string.IsNullOrEmpty(id)).ToList();
        LogPage(logger, added.Count, modified.Count, removed.Count, response.HasMore);
        return new SyncResult(added, modified, removed, response.NextCursor ?? cursor ?? string.Empty, response.HasMore)
        {
            IsHistoryComplete = PlaidMapping.IsHistoryComplete(response.TransactionsUpdateStatus),
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(string connectionId, CancellationToken ct)
    {
        var accounts = await GetAccountsAsync(connectionId, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return accounts.Select(a => PlaidMapping.ToProviderBalance(a, now)).OfType<ProviderBalance>().ToList();
    }

    /// <inheritdoc />
    public async Task<ConnectionHealth> GetHealthAsync(string connectionId, CancellationToken ct)
    {
        try
        {
            var (credentials, connection, environment) = await LoadAsync(connectionId).ConfigureAwait(false);
            var response = await CallAsync(
                () => api.ItemGetAsync(environment, new ItemGetRequest { ClientId = credentials.ClientId, Secret = credentials.Secret, AccessToken = connection.AccessToken }, ct),
                "item/get").ConfigureAwait(false);
            return response.Item?.Error is { ErrorCode.Length: > 0 } error
                ? new ConnectionHealth(PlaidMapping.StatusOf(error), error.ErrorCode, time.GetUtcNow())
                : new ConnectionHealth(SyncStatus.Ok, null, time.GetUtcNow());
        }
        catch (BankProviderException ex)
        {
            return new ConnectionHealth(ex.Status, ex.Code, time.GetUtcNow());
        }
    }

    /// <inheritdoc />
    public async Task UnlinkAsync(string connectionId, CancellationToken ct)
    {
        var (credentials, connection, environment) = await LoadAsync(connectionId).ConfigureAwait(false);
        try
        {
            await CallAsync(
                () => api.ItemRemoveAsync(environment, new ItemRemoveRequest { ClientId = credentials.ClientId, Secret = credentials.Secret, AccessToken = connection.AccessToken }, ct),
                "item/remove").ConfigureAwait(false);
        }
        catch (BankProviderException ex) when (ex.Code is "ITEM_NOT_FOUND" or "INVALID_ACCESS_TOKEN")
        {
            // Already gone at Plaid: nothing to remove there.
        }

        await secrets.DeleteAsync(SecretKeys.Connection(connectionId)).ConfigureAwait(false);
        LogUnlinked(logger);
    }

    private async Task<IReadOnlyList<Account>> GetAccountsAsync(string connectionId, CancellationToken ct)
    {
        var (credentials, connection, environment) = await LoadAsync(connectionId).ConfigureAwait(false);
        var response = await CallAsync(
            () => api.AccountsGetAsync(environment, new AccountsGetRequest { ClientId = credentials.ClientId, Secret = credentials.Secret, AccessToken = connection.AccessToken }, ct),
            "accounts/get").ConfigureAwait(false);
        return response.Accounts ?? [];
    }

    private async Task<(PlaidCredentials Credentials, ConnectionSecret Connection, PlaidEnvironment Environment)> LoadAsync(string connectionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        var credentials = await PlaidCredentials.LoadAsync(secrets).ConfigureAwait(false);
        var connection = await ConnectionSecret.LoadAsync(secrets, connectionId).ConfigureAwait(false);
        if (string.IsNullOrEmpty(connection.AccessToken))
        {
            throw new BankProviderException(SyncStatus.NeedsReauth, BankErrorCodes.MissingAccessToken, "The connection has no access token.");
        }

        return (credentials, connection, PlaidCredentials.ParseEnvironment(connection.Environment) ?? credentials.Environment);
    }

    // Runs a Plaid call: Plaid errors and transport failures become BankProviderException.
    private async Task<T> CallAsync<T>(Func<Task<T>> call, string operation)
        where T : ResponseBase
    {
        T response;
        try
        {
            response = await call().ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            LogFailed(logger, operation, BankErrorCodes.Network);
            throw new BankProviderException(SyncStatus.Error, BankErrorCodes.Network, $"Plaid {operation} could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            LogFailed(logger, operation, BankErrorCodes.Network);
            throw new BankProviderException(SyncStatus.Error, BankErrorCodes.Network, $"Plaid {operation} timed out.", ex);
        }
        catch (Polly.Timeout.TimeoutRejectedException ex)
        {
            LogFailed(logger, operation, BankErrorCodes.Network);
            throw new BankProviderException(SyncStatus.Error, BankErrorCodes.Network, $"Plaid {operation} timed out.", ex);
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException ex)
        {
            LogFailed(logger, operation, BankErrorCodes.Network);
            throw new BankProviderException(SyncStatus.Error, BankErrorCodes.Network, $"Plaid {operation} is failing; retrying later.", ex);
        }

        if (response.Error is { } error)
        {
            LogFailed(logger, operation, error.ErrorCode ?? BankErrorCodes.Unknown);
            throw PlaidMapping.ToException(error, operation);
        }

        if (!response.IsSuccessStatusCode)
        {
            LogFailed(logger, operation, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            throw new BankProviderException(SyncStatus.Error, BankErrorCodes.Unknown, $"Plaid {operation} returned HTTP {(int)response.StatusCode}.");
        }

        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plaid link started ({Mode}, {Environment})")]
    private static partial void LogLinkStarted(ILogger logger, LinkMode mode, PlaidEnvironment environment);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plaid link finished (succeeded: {Succeeded})")]
    private static partial void LogLinkFinished(ILogger logger, bool succeeded);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Plaid sync page: {Added} added, {Modified} modified, {Removed} removed, more: {HasMore}")]
    private static partial void LogPage(ILogger logger, int added, int modified, int removed, bool hasMore);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plaid {Operation} failed: {Code}")]
    private static partial void LogFailed(ILogger logger, string operation, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plaid item removed")]
    private static partial void LogUnlinked(ILogger logger);
}

/// <summary>The Plaid keys from the secret store.</summary>
internal sealed record PlaidCredentials(string ClientId, string Secret, PlaidEnvironment Environment)
{
    /// <summary>Loads the keys; throws <see cref="BankProviderException"/> with <see cref="BankErrorCodes.MissingCredentials"/> when absent.</summary>
    public static async Task<PlaidCredentials> LoadAsync(ISecretStore store)
    {
        var clientId = await store.GetAsync(SecretKeys.PlaidClientId).ConfigureAwait(false);
        var secret = await store.GetAsync(SecretKeys.PlaidSecret).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret))
        {
            throw new BankProviderException(SyncStatus.Error, BankErrorCodes.MissingCredentials, "Plaid keys are not set.");
        }

        var environment = ParseEnvironment(await store.GetAsync(SecretKeys.PlaidEnvironment).ConfigureAwait(false)) ?? PlaidEnvironment.Sandbox;
        return new PlaidCredentials(clientId.Trim(), secret.Trim(), environment);
    }

    /// <summary>"sandbox" or "production".</summary>
    public static string EnvironmentName(PlaidEnvironment environment) => environment == PlaidEnvironment.Production ? "production" : "sandbox";

    /// <summary>The environment named <paramref name="name"/>, or null.</summary>
    public static PlaidEnvironment? ParseEnvironment(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "sandbox" => PlaidEnvironment.Sandbox,
        "production" => PlaidEnvironment.Production,
        _ => null,
    };
}
