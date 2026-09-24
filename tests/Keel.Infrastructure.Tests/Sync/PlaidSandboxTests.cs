using Going.Plaid.Entity;
using Going.Plaid.Item;
using Going.Plaid.Sandbox;
using Keel.Application.Sync;
using Keel.Domain;
using Keel.Infrastructure.Sync;
using Keel.Infrastructure.Sync.Plaid;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Infrastructure.Tests.Sync;

/// <summary>
/// F-TXN-3 end to end against the real Plaid sandbox. Runs only with
/// <c>KEEL_PLAID_CLIENT_ID</c> and <c>KEEL_PLAID_SECRET</c> (sandbox keys) set; otherwise skipped.
/// Hosted Link needs a browser, so the item is created with <c>/sandbox/public_token/create</c>
/// (user_good / pass_good) and the rest runs through the provider and the sync service.
/// </summary>
public sealed class PlaidSandboxTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [EnvFact("KEEL_PLAID_CLIENT_ID", "KEEL_PLAID_SECRET")]
    public async Task Sandbox_link_sync_reconnect_state_and_unlink()
    {
        var clientId = Environment.GetEnvironmentVariable("KEEL_PLAID_CLIENT_ID")!;
        var secret = Environment.GetEnvironmentVariable("KEEL_PLAID_SECRET")!;
        await using var host = await SyncTestHost.CreateAsync(withKeys: false, configure: s => s.AddHttpClient(GoingPlaidApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()));
        var credentials = host.Get<IBankCredentialsService>();
        await credentials.SetPlaidClientIdAsync(clientId, Ct);
        await credentials.SetPlaidSecretAsync(secret, Ct);
        await credentials.SetPlaidEnvironmentAsync(PlaidEnvironment.Sandbox, Ct);
        var api = host.Get<IPlaidApi>();

        // Hosted Link: a real link token with a hosted URL.
        var session = await host.Sync.BeginLinkAsync("plaid", Ct);
        session.LinkUrl.Scheme.ShouldBe("https");

        // Link: sandbox item for user_good, exchanged and stored like CompleteLinkAsync does.
        var created = await api.SandboxPublicTokenCreateAsync(
            new SandboxPublicTokenCreateRequest { ClientId = clientId, Secret = secret, InstitutionId = "ins_109508", InitialProducts = [Products.Transactions] }, Ct);
        created.Error.ShouldBeNull();
        var exchange = await api.ItemPublicTokenExchangeAsync(PlaidEnvironment.Sandbox, new ItemPublicTokenExchangeRequest { ClientId = clientId, Secret = secret, PublicToken = created.PublicToken }, Ct);
        exchange.Error.ShouldBeNull();
        var connectionId = Guid.CreateVersion7();
        await new ConnectionSecret { Provider = "plaid", Environment = "sandbox", AccessToken = exchange.AccessToken, ItemId = exchange.ItemId }.SaveAsync(host.Secrets, connectionId.ToString());

        // List accounts and save the mapping.
        var provider = host.Get<IEnumerable<IBankDataProvider>>().Single(p => p.ProviderId == "plaid");
        var accounts = await provider.ListAccountsAsync(connectionId.ToString(), Ct);
        accounts.ShouldNotBeEmpty();
        var balances = await provider.GetBalancesAsync(connectionId.ToString(), Ct);
        var pending = new PendingConnection("plaid", connectionId, "Sandbox bank", exchange.ItemId, accounts.Select(a => new PendingAccount(a, balances.FirstOrDefault(b => b.ProviderAccountId == a.ProviderAccountId), null)).ToList());
        var connection = await host.Sync.SaveLinkAsync(pending, accounts.Select(a => new AccountLinkChoice(a.ProviderAccountId, AccountLinkAction.CreateNew, NewName: a.Name, NewType: a.SuggestedType)).ToList(), Ct);

        // Initial sync: the sandbox prepares transactions asynchronously, so poll for them.
        var added = 0;
        for (var attempt = 0; attempt < 30 && added == 0; attempt++)
        {
            var result = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
            result.Succeeded.ShouldBeTrue(result.ErrorCode);
            added += result.Added;
            if (added == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        added.ShouldBeGreaterThan(0);
        var cursor = (await host.ConnectionAsync(connection.Id)).Cursor;
        cursor.ShouldNotBeNullOrEmpty();
        var linked = (await host.Sync.GetConnectionsAsync(Ct)).Single().Accounts;
        linked.ShouldContain(a => a.ReportedBalance != null);
        var countAfterInitial = (await Task.WhenAll(linked.Select(a => host.RowsAsync(a.AccountId)))).Sum(r => r.Count);

        // Incremental: the cursor holds and nothing is duplicated.
        var incremental = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        incremental.Succeeded.ShouldBeTrue();
        incremental.Added.ShouldBe(0);
        (await Task.WhenAll(linked.Select(a => host.RowsAsync(a.AccountId)))).Sum(r => r.Count).ShouldBe(countAfterInitial);

        // ITEM_LOGIN_REQUIRED: a reconnect state, not an exception.
        var reset = await api.SandboxItemResetLoginAsync(new SandboxItemResetLoginRequest { ClientId = clientId, Secret = secret, AccessToken = exchange.AccessToken }, Ct);
        reset.ResetLogin.ShouldBeTrue();
        var broken = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        (broken.Status, broken.ErrorCode).ShouldBe((SyncStatus.NeedsReauth, "ITEM_LOGIN_REQUIRED"));
        (await host.AccountAsync(linked[0].AccountId))!.SyncStatus.ShouldBe(SyncStatus.NeedsReauth);
        (await provider.GetHealthAsync(connection.Id.ToString(), Ct)).Status.ShouldBe(SyncStatus.NeedsReauth);
        var reconnect = await host.Sync.BeginReconnectAsync(connection.Id, Ct);
        reconnect.ExistingConnectionId.ShouldBe(connection.Id.ToString());

        // Unlink: the item goes, local transactions stay.
        await host.Sync.UnlinkAsync(connection.Id, Ct);
        host.Secrets.Contains(SecretKeys.Connection(connection.Id)).ShouldBeFalse();
        (await Task.WhenAll(linked.Select(a => host.RowsAsync(a.AccountId)))).Sum(r => r.Count).ShouldBe(countAfterInitial);
        var gone = await api.ItemGetAsync(PlaidEnvironment.Sandbox, new ItemGetRequest { ClientId = clientId, Secret = secret, AccessToken = exchange.AccessToken }, Ct);
        gone.Error.ShouldNotBeNull();
    }
}
