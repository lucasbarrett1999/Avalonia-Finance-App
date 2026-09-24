using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Domain;
using Microsoft.Extensions.Logging;
using EntityIds = Keel.Domain.Entities.EntityIds;

namespace Keel.Infrastructure.Sync.SimpleFin;

/// <summary>Tunables of the SimpleFIN provider.</summary>
public sealed record SimpleFinProviderOptions
{
    /// <summary>History fetched by the first sync.</summary>
    public int InitialDays { get; init; } = 90;

    /// <summary>Days re-read before the last sync so late postings and pending-to-posted changes are seen.</summary>
    public int OverlapDays { get; init; } = 14;

    /// <summary>Where the user manages the bridge (opened for a reconnect).</summary>
    public Uri BridgeUrl { get; init; } = new("https://beta-bridge.simplefin.org/");
}

/// <summary>
/// The SimpleFIN Bridge provider (F-TXN-3, P1): designed for local apps, no developer secret. A
/// one-time setup token (from Settings) is claimed for an access URL, which is stored in the secret
/// store as the connection's secret; syncs read <c>/accounts?start-date=</c> with an overlap window
/// (SimpleFIN has no cursor or removals, so the cursor is the time of the last fetch and provider
/// ids dedup the overlap). The access URL's credentials go in a Basic header, never in a logged URL.
/// </summary>
public sealed partial class SimpleFinProvider(
    IHttpClientFactory httpClientFactory,
    ISecretStore secrets,
    TimeProvider time,
    ILogger<SimpleFinProvider> logger,
    SimpleFinProviderOptions? options = null) : IBankDataProvider
{
    /// <summary>The provider id.</summary>
    public const string Id = "simplefin";

    /// <summary>The named <see cref="HttpClient"/> (resilience, no request logging).</summary>
    public const string HttpClientName = "SimpleFin";

    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString };
    private readonly SimpleFinProviderOptions _options = options ?? new SimpleFinProviderOptions();

    /// <inheritdoc />
    public string ProviderId => Id;

    /// <summary>Decodes a setup token (base64 of an https claim URL); null when malformed.</summary>
    public static Uri? ClaimUrlOf(string? setupToken)
    {
        if (string.IsNullOrWhiteSpace(setupToken))
        {
            return null;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(setupToken.Trim()));
            return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Splits an access URL into the URL without credentials and the Basic credentials.</summary>
    public static (Uri BaseUrl, string? Basic) SplitAccessUrl(Uri accessUrl)
    {
        ArgumentNullException.ThrowIfNull(accessUrl);
        var builder = new UriBuilder(accessUrl) { UserName = string.Empty, Password = string.Empty };
        var basic = string.IsNullOrEmpty(accessUrl.UserInfo)
            ? null
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(accessUrl.UserInfo)));
        return (builder.Uri, basic);
    }

    /// <inheritdoc />
    public async Task<LinkSession> BeginLinkAsync(LinkMode mode, string? existingConnectionId, CancellationToken ct)
    {
        if (mode == LinkMode.Update)
        {
            // SimpleFIN repairs happen at the bridge; Keel then checks the connection until it works.
            ArgumentException.ThrowIfNullOrEmpty(existingConnectionId);
            return new LinkSession(Id, existingConnectionId, _options.BridgeUrl, time.GetUtcNow().AddMinutes(10), existingConnectionId);
        }

        var token = await secrets.GetAsync(SecretKeys.SimpleFinSetupToken).ConfigureAwait(false);
        var claim = ClaimUrlOf(token)
            ?? throw new BankProviderException(SyncStatus.Error, string.IsNullOrWhiteSpace(token) ? BankErrorCodes.MissingCredentials : BankErrorCodes.InvalidSetupToken, "The SimpleFIN setup token is missing or malformed.");

        string accessUrl;
        using (var request = new HttpRequestMessage(HttpMethod.Post, claim) { Content = new ByteArrayContent([]) })
        {
            using var response = await SendAsync(request, "claim", ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new BankProviderException(SyncStatus.Error, BankErrorCodes.InvalidSetupToken, "The SimpleFIN setup token was already claimed or is invalid.");
            }

            EnsureSuccess(response, "claim");
            accessUrl = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        }

        if (!Uri.TryCreate(accessUrl, UriKind.Absolute, out var access) || access.Scheme != Uri.UriSchemeHttps)
        {
            throw new BankProviderException(SyncStatus.Error, BankErrorCodes.InvalidSetupToken, "SimpleFIN returned an unusable access URL.");
        }

        var connectionId = EntityIds.New().ToString();
        await new ConnectionSecret { Provider = Id, AccessUrl = access.ToString() }.SaveAsync(secrets, connectionId).ConfigureAwait(false);
        await secrets.DeleteAsync(SecretKeys.SimpleFinSetupToken).ConfigureAwait(false);
        LogClaimed(logger);
        return new LinkSession(Id, connectionId, _options.BridgeUrl, time.GetUtcNow().AddMinutes(10), null) { OpensBrowser = false };
    }

    /// <inheritdoc />
    public async Task<LinkResult> CompleteLinkAsync(LinkSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var connectionId = session.ExistingConnectionId ?? session.SessionToken;
        var deadline = session.ExpiresAt;
        while (true)
        {
            try
            {
                var set = await FetchAsync(connectionId, balancesOnly: true, startDate: null, ct).ConfigureAwait(false);
                var org = set.Accounts.Select(a => a.Org).FirstOrDefault(o => o is not null);
                return new LinkResult(true, connectionId, org?.Name ?? org?.Domain, null) { ExternalItemId = org?.Domain ?? Id };
            }
            catch (BankProviderException ex) when (session.ExistingConnectionId is not null && ex.Status == SyncStatus.NeedsReauth && time.GetUtcNow() < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), time, ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderAccount>> ListAccountsAsync(string connectionId, CancellationToken ct)
    {
        var set = await FetchAsync(connectionId, balancesOnly: true, startDate: null, ct).ConfigureAwait(false);
        return set.Accounts.Select(a => new ProviderAccount(a.Id, a.Name ?? a.Id, null, GuessType(a.Name), CurrencyOf(a.Currency))).ToList();
    }

    /// <inheritdoc />
    public async Task<SyncResult> SyncTransactionsAsync(string connectionId, string? cursor, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var start = long.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var last)
            ? DateTimeOffset.FromUnixTimeSeconds(last).AddDays(-_options.OverlapDays)
            : now.AddDays(-_options.InitialDays);
        var set = await FetchAsync(connectionId, balancesOnly: false, start, ct).ConfigureAwait(false);
        var added = new List<ProviderTransaction>();
        foreach (var account in set.Accounts)
        {
            var currency = CurrencyOf(account.Currency);
            foreach (var t in account.Transactions ?? [])
            {
                if (string.IsNullOrEmpty(t.Id) || !decimal.TryParse(t.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                {
                    continue;
                }

                var posted = t.Posted is > 0 ? t.Posted.Value : t.TransactedAt ?? now.ToUnixTimeSeconds();
                var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(posted), time.LocalTimeZone).DateTime);
                added.Add(new ProviderTransaction(
                    t.Id,
                    account.Id,
                    date,
                    Money.FromDecimal(amount, currency).Amount,
                    (t.Payee ?? t.Description ?? string.Empty).Trim(),
                    string.IsNullOrWhiteSpace(t.Memo) ? null : t.Memo.Trim(),
                    t.Pending ?? t.Posted is null or 0,
                    null));
            }
        }

        LogFetched(logger, added.Count);
        return new SyncResult(added, [], [], now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), HasMore: false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(string connectionId, CancellationToken ct)
    {
        var set = await FetchAsync(connectionId, balancesOnly: true, startDate: null, ct).ConfigureAwait(false);
        var balances = new List<ProviderBalance>();
        foreach (var a in set.Accounts)
        {
            var currency = CurrencyOf(a.Currency);
            if (!decimal.TryParse(a.Balance, NumberStyles.Number, CultureInfo.InvariantCulture, out var balance))
            {
                continue;
            }

            long? available = decimal.TryParse(a.AvailableBalance, NumberStyles.Number, CultureInfo.InvariantCulture, out var av) ? Money.FromDecimal(av, currency).Amount : null;
            var asOf = a.BalanceDate is > 0 ? DateTimeOffset.FromUnixTimeSeconds(a.BalanceDate.Value) : time.GetUtcNow();
            balances.Add(new ProviderBalance(a.Id, Money.FromDecimal(balance, currency).Amount, available, currency, asOf));
        }

        return balances;
    }

    /// <inheritdoc />
    public async Task<ConnectionHealth> GetHealthAsync(string connectionId, CancellationToken ct)
    {
        try
        {
            await FetchAsync(connectionId, balancesOnly: true, startDate: null, ct).ConfigureAwait(false);
            return new ConnectionHealth(SyncStatus.Ok, null, time.GetUtcNow());
        }
        catch (BankProviderException ex)
        {
            return new ConnectionHealth(ex.Status, ex.Code, time.GetUtcNow());
        }
    }

    /// <inheritdoc />
    public async Task UnlinkAsync(string connectionId, CancellationToken ct)
    {
        // SimpleFIN has no revoke call: forgetting the access URL ends Keel's access; the user can
        // also remove the app at the bridge.
        await secrets.DeleteAsync(SecretKeys.Connection(connectionId)).ConfigureAwait(false);
        LogUnlinked(logger);
    }

    /// <summary>A Keel type guessed from a SimpleFIN account name (SimpleFIN reports no type).</summary>
    public static AccountType GuessType(string? name)
    {
        var n = name?.ToUpperInvariant() ?? string.Empty;
        return n.Contains("CREDIT", StringComparison.Ordinal) || n.Contains("CARD", StringComparison.Ordinal) || n.Contains("VISA", StringComparison.Ordinal) || n.Contains("MASTERCARD", StringComparison.Ordinal) ? AccountType.CreditCard
            : n.Contains("SAVING", StringComparison.Ordinal) ? AccountType.Savings
            : n.Contains("LOAN", StringComparison.Ordinal) || n.Contains("MORTGAGE", StringComparison.Ordinal) ? AccountType.Loan
            : n.Contains("BROKERAGE", StringComparison.Ordinal) || n.Contains("401", StringComparison.Ordinal) || n.Contains("IRA", StringComparison.Ordinal) ? AccountType.Investment
            : AccountType.Checking;
    }

    private static string CurrencyOf(string? code) =>
        Currency.IsValidCode(code?.Trim().ToUpperInvariant()) ? Currency.Normalize(code!) : Currency.Default;

    private async Task<AccountSet> FetchAsync(string connectionId, bool balancesOnly, DateTimeOffset? startDate, CancellationToken ct)
    {
        var secret = await ConnectionSecret.LoadAsync(secrets, connectionId).ConfigureAwait(false);
        if (!Uri.TryCreate(secret.AccessUrl, UriKind.Absolute, out var access))
        {
            throw new BankProviderException(SyncStatus.NeedsReauth, BankErrorCodes.MissingAccessToken, "The connection has no SimpleFIN access URL.");
        }

        var (baseUrl, basic) = SplitAccessUrl(access);
        var query = new StringBuilder("accounts?pending=1");
        if (balancesOnly)
        {
            query.Append("&balances-only=1");
        }

        if (startDate is { } start)
        {
            query.Append(CultureInfo.InvariantCulture, $"&start-date={start.ToUnixTimeSeconds()}");
        }

        var url = new Uri(baseUrl.ToString().TrimEnd('/') + "/" + query);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (basic is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        using var response = await SendAsync(request, "accounts", ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new BankProviderException(SyncStatus.NeedsReauth, SimpleFinErrorCodes.AccessRevoked, "SimpleFIN refused the access URL.");
        }

        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            throw new BankProviderException(SyncStatus.Error, SimpleFinErrorCodes.PaymentRequired, "The SimpleFIN Bridge subscription needs attention.");
        }

        EnsureSuccess(response, "accounts");
        AccountSet set;
        try
        {
            set = await response.Content.ReadFromJsonAsync<AccountSet>(Json, ct).ConfigureAwait(false) ?? new AccountSet();
        }
        catch (JsonException ex)
        {
            throw new BankProviderException(SyncStatus.Error, SimpleFinErrorCodes.BadResponse, "SimpleFIN returned an unreadable response.", ex);
        }

        if (set.Errors is { Count: > 0 } && set.Accounts.Count == 0)
        {
            LogBridgeErrors(logger, set.Errors.Count);
            throw new BankProviderException(SyncStatus.NeedsReauth, SimpleFinErrorCodes.BridgeError, "SimpleFIN reported errors for the connection.");
        }

        return set;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string operation, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            return await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            LogFailed(logger, operation, BankErrorCodes.Network);
            throw new BankProviderException(SyncStatus.Error, BankErrorCodes.Network, $"SimpleFIN {operation} could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            LogFailed(logger, operation, BankErrorCodes.Network);
            throw new BankProviderException(SyncStatus.Error, BankErrorCodes.Network, $"SimpleFIN {operation} timed out.", ex);
        }
        catch (Polly.Timeout.TimeoutRejectedException ex)
        {
            LogFailed(logger, operation, BankErrorCodes.Network);
            throw new BankProviderException(SyncStatus.Error, BankErrorCodes.Network, $"SimpleFIN {operation} timed out.", ex);
        }
    }

    private void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            var code = "HTTP_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            LogFailed(logger, operation, code);
            throw new BankProviderException(SyncStatus.Error, code, $"SimpleFIN {operation} returned HTTP {(int)response.StatusCode}.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SimpleFIN setup token claimed")]
    private static partial void LogClaimed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SimpleFIN returned {Count} transactions")]
    private static partial void LogFetched(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SimpleFIN {Operation} failed: {Code}")]
    private static partial void LogFailed(ILogger logger, string operation, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SimpleFIN reported {Count} errors and no accounts")]
    private static partial void LogBridgeErrors(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "SimpleFIN connection forgotten")]
    private static partial void LogUnlinked(ILogger logger);

    // The SimpleFIN protocol's account set (https://www.simplefin.org/protocol.html).
    private sealed record AccountSet
    {
        [JsonPropertyName("errors")]
        public List<string>? Errors { get; init; }

        [JsonPropertyName("accounts")]
        public List<SfAccount> Accounts { get; init; } = [];
    }

    private sealed record SfOrg
    {
        [JsonPropertyName("domain")]
        public string? Domain { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed record SfAccount
    {
        [JsonPropertyName("org")]
        public SfOrg? Org { get; init; }

        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("currency")]
        public string? Currency { get; init; }

        [JsonPropertyName("balance")]
        public string? Balance { get; init; }

        [JsonPropertyName("available-balance")]
        public string? AvailableBalance { get; init; }

        [JsonPropertyName("balance-date")]
        public long? BalanceDate { get; init; }

        [JsonPropertyName("transactions")]
        public List<SfTransaction>? Transactions { get; init; }
    }

    private sealed record SfTransaction
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("posted")]
        public long? Posted { get; init; }

        [JsonPropertyName("transacted_at")]
        public long? TransactedAt { get; init; }

        [JsonPropertyName("amount")]
        public string? Amount { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("payee")]
        public string? Payee { get; init; }

        [JsonPropertyName("memo")]
        public string? Memo { get; init; }

        [JsonPropertyName("pending")]
        public bool? Pending { get; init; }
    }
}

/// <summary>SimpleFIN error codes recorded on a connection.</summary>
public static class SimpleFinErrorCodes
{
    /// <summary>The access URL was revoked (reconnect at the bridge, or link again).</summary>
    public const string AccessRevoked = "SIMPLEFIN_ACCESS_REVOKED";

    /// <summary>The bridge subscription needs payment.</summary>
    public const string PaymentRequired = "SIMPLEFIN_PAYMENT_REQUIRED";

    /// <summary>The bridge reported errors (usually the bank needs a new sign-in at the bridge).</summary>
    public const string BridgeError = "SIMPLEFIN_BRIDGE_ERROR";

    /// <summary>The response could not be read.</summary>
    public const string BadResponse = "SIMPLEFIN_BAD_RESPONSE";
}
