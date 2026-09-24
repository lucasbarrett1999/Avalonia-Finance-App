using Going.Plaid;
using Going.Plaid.Accounts;
using Going.Plaid.Item;
using Going.Plaid.Link;
using Going.Plaid.Sandbox;
using Going.Plaid.Transactions;
using Keel.Application.Sync;

namespace Keel.Infrastructure.Sync.Plaid;

/// <summary>
/// The Plaid HTTP calls Keel makes (PRD 7.5). Requests carry the client id, secret and access
/// token; responses carry Plaid errors in <see cref="ResponseBase.Error"/> instead of throwing.
/// Tests fake the HTTP layer underneath <see cref="GoingPlaidApi"/> or this interface.
/// </summary>
public interface IPlaidApi
{
    /// <summary><c>/link/token/create</c>.</summary>
    Task<LinkTokenCreateResponse> LinkTokenCreateAsync(PlaidEnvironment environment, LinkTokenCreateRequest request, CancellationToken ct);

    /// <summary><c>/link/token/get</c>.</summary>
    Task<LinkTokenGetResponse> LinkTokenGetAsync(PlaidEnvironment environment, LinkTokenGetRequest request, CancellationToken ct);

    /// <summary><c>/item/public_token/exchange</c>.</summary>
    Task<ItemPublicTokenExchangeResponse> ItemPublicTokenExchangeAsync(PlaidEnvironment environment, ItemPublicTokenExchangeRequest request, CancellationToken ct);

    /// <summary><c>/accounts/get</c> (cached balances; no Balance product charge).</summary>
    Task<AccountsGetResponse> AccountsGetAsync(PlaidEnvironment environment, AccountsGetRequest request, CancellationToken ct);

    /// <summary><c>/transactions/sync</c>.</summary>
    Task<TransactionsSyncResponse> TransactionsSyncAsync(PlaidEnvironment environment, TransactionsSyncRequest request, CancellationToken ct);

    /// <summary><c>/item/get</c>.</summary>
    Task<ItemGetResponse> ItemGetAsync(PlaidEnvironment environment, ItemGetRequest request, CancellationToken ct);

    /// <summary><c>/item/remove</c>.</summary>
    Task<ItemRemoveResponse> ItemRemoveAsync(PlaidEnvironment environment, ItemRemoveRequest request, CancellationToken ct);

    /// <summary><c>/sandbox/public_token/create</c> (sandbox tests only).</summary>
    Task<SandboxPublicTokenCreateResponse> SandboxPublicTokenCreateAsync(SandboxPublicTokenCreateRequest request, CancellationToken ct);

    /// <summary><c>/sandbox/item/reset_login</c> (sandbox tests only).</summary>
    Task<SandboxItemResetLoginResponse> SandboxItemResetLoginAsync(SandboxItemResetLoginRequest request, CancellationToken ct);
}

/// <summary>
/// <see cref="IPlaidApi"/> over Going.Plaid's <see cref="PlaidClient"/>, one client per environment,
/// with HTTP from <see cref="IHttpClientFactory"/> (the <see cref="HttpClientName"/> client carries
/// the resilience pipeline). Going.Plaid takes no cancellation token, so calls stop waiting on
/// cancellation and the request finishes in the background.
/// </summary>
public sealed class GoingPlaidApi(IHttpClientFactory httpClientFactory) : IPlaidApi
{
    /// <summary>The named <see cref="HttpClient"/> Going.Plaid asks the factory for.</summary>
    public const string HttpClientName = "PlaidClient";

    private readonly PlaidClient _sandbox = new(Going.Plaid.Environment.Sandbox, httpClientFactory: httpClientFactory);
    private readonly PlaidClient _production = new(Going.Plaid.Environment.Production, httpClientFactory: httpClientFactory);

    /// <inheritdoc />
    public Task<LinkTokenCreateResponse> LinkTokenCreateAsync(PlaidEnvironment environment, LinkTokenCreateRequest request, CancellationToken ct) =>
        Client(environment).LinkTokenCreateAsync(request).WaitAsync(ct);

    /// <inheritdoc />
    public Task<LinkTokenGetResponse> LinkTokenGetAsync(PlaidEnvironment environment, LinkTokenGetRequest request, CancellationToken ct) =>
        Client(environment).LinkTokenGetAsync(request).WaitAsync(ct);

    /// <inheritdoc />
    public Task<ItemPublicTokenExchangeResponse> ItemPublicTokenExchangeAsync(PlaidEnvironment environment, ItemPublicTokenExchangeRequest request, CancellationToken ct) =>
        Client(environment).ItemPublicTokenExchangeAsync(request).WaitAsync(ct);

    /// <inheritdoc />
    public Task<AccountsGetResponse> AccountsGetAsync(PlaidEnvironment environment, AccountsGetRequest request, CancellationToken ct) =>
        Client(environment).AccountsGetAsync(request).WaitAsync(ct);

    /// <inheritdoc />
    public Task<TransactionsSyncResponse> TransactionsSyncAsync(PlaidEnvironment environment, TransactionsSyncRequest request, CancellationToken ct) =>
        Client(environment).TransactionsSyncAsync(request).WaitAsync(ct);

    /// <inheritdoc />
    public Task<ItemGetResponse> ItemGetAsync(PlaidEnvironment environment, ItemGetRequest request, CancellationToken ct) =>
        Client(environment).ItemGetAsync(request).WaitAsync(ct);

    /// <inheritdoc />
    public Task<ItemRemoveResponse> ItemRemoveAsync(PlaidEnvironment environment, ItemRemoveRequest request, CancellationToken ct) =>
        Client(environment).ItemRemoveAsync(request).WaitAsync(ct);

    /// <inheritdoc />
    public Task<SandboxPublicTokenCreateResponse> SandboxPublicTokenCreateAsync(SandboxPublicTokenCreateRequest request, CancellationToken ct) =>
        _sandbox.SandboxPublicTokenCreateAsync(request).WaitAsync(ct);

    /// <inheritdoc />
    public Task<SandboxItemResetLoginResponse> SandboxItemResetLoginAsync(SandboxItemResetLoginRequest request, CancellationToken ct) =>
        _sandbox.SandboxItemResetLoginAsync(request).WaitAsync(ct);

    private PlaidClient Client(PlaidEnvironment environment) => environment == PlaidEnvironment.Production ? _production : _sandbox;
}
