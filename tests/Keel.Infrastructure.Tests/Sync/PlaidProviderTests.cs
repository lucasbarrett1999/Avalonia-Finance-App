using Going.Plaid.Entity;
using Keel.Application.Sync;
using Keel.Domain;
using Keel.Infrastructure.Sync.Plaid;
using PlaidAccountType = Going.Plaid.Entity.AccountType;

namespace Keel.Infrastructure.Tests.Sync;

/// <summary>The Plaid provider's mapping and HTTP behaviour against the fake Plaid server.</summary>
public sealed class PlaidProviderTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData(PlaidAccountType.Depository, AccountSubtype.Checking, Keel.Domain.AccountType.Checking)]
    [InlineData(PlaidAccountType.Depository, AccountSubtype.Savings, Keel.Domain.AccountType.Savings)]
    [InlineData(PlaidAccountType.Depository, AccountSubtype.MoneyMarket, Keel.Domain.AccountType.Savings)]
    [InlineData(PlaidAccountType.Depository, AccountSubtype.Prepaid, Keel.Domain.AccountType.Checking)]
    [InlineData(PlaidAccountType.Credit, AccountSubtype.CreditCard, Keel.Domain.AccountType.CreditCard)]
    [InlineData(PlaidAccountType.Loan, AccountSubtype.Mortgage, Keel.Domain.AccountType.Loan)]
    [InlineData(PlaidAccountType.Loan, AccountSubtype.LineOfCredit, Keel.Domain.AccountType.LineOfCredit)]
    [InlineData(PlaidAccountType.Investment, AccountSubtype._401k, Keel.Domain.AccountType.Investment)]
    [InlineData(PlaidAccountType.Other, AccountSubtype.Other, Keel.Domain.AccountType.OtherAsset)]
    public void Account_types_map_to_Keel_types(PlaidAccountType type, AccountSubtype subtype, Keel.Domain.AccountType expected) =>
        PlaidMapping.SuggestType(type, subtype).ShouldBe(expected);

    [Fact]
    public void Plaid_amounts_flip_sign_into_minor_units_of_their_currency()
    {
        var currencies = new Dictionary<string, string> { ["a"] = "USD" };
        var outflow = PlaidMapping.ToProviderTransaction(new Transaction { TransactionId = "t", AccountId = "a", Amount = 12.345m, IsoCurrencyCode = "USD", Date = new DateOnly(2026, 9, 1), MerchantName = "Cafe" }, currencies);
        outflow.Amount.ShouldBe(-1234);
        outflow.PayeeRaw.ShouldBe("Cafe");

        var yen = PlaidMapping.ToProviderTransaction(new Transaction { TransactionId = "y", AccountId = "a", Amount = -500m, IsoCurrencyCode = "JPY", Date = new DateOnly(2026, 9, 1) }, currencies);
        yen.Amount.ShouldBe(500);

        var crypto = PlaidMapping.ToProviderTransaction(new Transaction { TransactionId = "c", AccountId = "a", Amount = 1m, UnofficialCurrencyCode = "BTC", Date = new DateOnly(2026, 9, 1) }, currencies);
        crypto.Amount.ShouldBe(-100);
    }

    [Fact]
    public void Liability_balances_are_negative()
    {
        var now = DateTimeOffset.UtcNow;
        var card = new Account { AccountId = "c", Type = PlaidAccountType.Credit, Balances = new AccountBalance { Current = 410m, Available = 590m, IsoCurrencyCode = "USD" } };
        var balance = PlaidMapping.ToProviderBalance(card, now)!;
        (balance.Current, balance.Available).ShouldBe((-41_000L, (long?)-59_000));
        PlaidMapping.ToProviderBalance(new Account { AccountId = "x", Type = PlaidAccountType.Depository, Balances = new AccountBalance() }, now).ShouldBeNull();
    }

    [Theory]
    [InlineData("ITEM_LOGIN_REQUIRED", SyncStatus.NeedsReauth)]
    [InlineData("PENDING_EXPIRATION", SyncStatus.NeedsReauth)]
    [InlineData("INSTITUTION_DOWN", SyncStatus.Error)]
    [InlineData("RATE_LIMIT_EXCEEDED", SyncStatus.Error)]
    public void Plaid_errors_map_to_connection_health(string code, SyncStatus expected) =>
        PlaidMapping.StatusOf(new PlaidError { ErrorCode = code }).ShouldBe(expected);

    [Fact]
    public void History_is_complete_only_after_the_historical_update() =>
        new[] { TransactionsUpdateStatus.NotReady, TransactionsUpdateStatus.InitialUpdateComplete, TransactionsUpdateStatus.HistoricalUpdateComplete }
            .Select(PlaidMapping.IsHistoryComplete).ShouldBe([false, false, true]);

    [Fact]
    public async Task Begin_link_asks_for_Hosted_Link_and_transactions_with_the_users_keys()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var provider = host.Get<IEnumerable<IBankDataProvider>>().Single(p => p.ProviderId == "plaid");

        var session = await provider.BeginLinkAsync(LinkMode.New, null, Ct);

        session.LinkUrl.Host.ShouldBe("hosted.plaid.com");
        session.OpensBrowser.ShouldBeTrue();
        session.ExistingConnectionId.ShouldBeNull();
        var body = host.Plaid.Bodies.Last();
        body["hosted_link"].ShouldNotBeNull();
        body["products"]!.AsArray().Select(p => (string?)p).ShouldBe(["transactions"]);
        ((int?)body["transactions"]!["days_requested"]).ShouldBe(90);
        ((string?)body["client_id"]).ShouldBe(FakePlaidServer.ClientId);
        ((string?)body["user"]!["client_user_id"]).ShouldBe(PlaidProvider.ClientUserId);
    }

    [Fact]
    public async Task Complete_link_polls_until_the_user_finishes()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var provider = host.Get<IEnumerable<IBankDataProvider>>().Single(p => p.ProviderId == "plaid");
        var session = await provider.BeginLinkAsync(LinkMode.New, null, Ct);

        var completing = provider.CompleteLinkAsync(session, Ct);
        await Task.Delay(100);
        completing.IsCompleted.ShouldBeFalse();
        var item = host.Plaid.CompleteLink(session.SessionToken)!;
        var result = await completing;

        result.Succeeded.ShouldBeTrue();
        result.InstitutionName.ShouldBe(item.InstitutionName);
        result.ExternalItemId.ShouldBe(item.ItemId);
        host.Plaid.Requests.Count(r => r == "/link/token/get").ShouldBeGreaterThan(1);
        (await provider.ListAccountsAsync(result.ConnectionId!, Ct)).Select(a => (a.ProviderAccountId, a.Mask, a.SuggestedType))
            .ShouldBe([("acc-checking", "0000", Keel.Domain.AccountType.Checking), ("acc-credit", "3333", Keel.Domain.AccountType.CreditCard)]);
    }

    [Fact]
    public async Task Cancelling_a_link_stops_polling()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var provider = host.Get<IEnumerable<IBankDataProvider>>().Single(p => p.ProviderId == "plaid");
        var session = await provider.BeginLinkAsync(LinkMode.New, null, Ct);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Should.ThrowAsync<OperationCanceledException>(provider.CompleteLinkAsync(session, cts.Token));
    }

    [Fact]
    public async Task Health_reflects_item_errors()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var (connection, item) = await host.LinkAsync();
        var provider = host.Get<IEnumerable<IBankDataProvider>>().Single(p => p.ProviderId == "plaid");

        (await provider.GetHealthAsync(connection.Id.ToString(), Ct)).Status.ShouldBe(SyncStatus.Ok);
        item.LoginRequired = true;
        var health = await provider.GetHealthAsync(connection.Id.ToString(), Ct);
        (health.Status, health.Message).ShouldBe((SyncStatus.NeedsReauth, "ITEM_LOGIN_REQUIRED"));
    }

    [Fact]
    public async Task Wrong_keys_are_an_error_with_Plaids_code()
    {
        await using var host = await SyncTestHost.CreateAsync();
        await host.Get<IBankCredentialsService>().SetPlaidSecretAsync("wrong", Ct);
        var ex = await Should.ThrowAsync<BankProviderException>(host.Sync.BeginLinkAsync("plaid", Ct));
        (ex.Code, ex.Status).ShouldBe(("INVALID_API_KEYS", SyncStatus.Error));
        ex.Message.ShouldNotContain("wrong");
    }

    [Fact]
    public async Task Unlink_deletes_the_token_even_when_the_item_is_already_gone()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var (connection, item) = await host.LinkAsync();
        var provider = host.Get<IEnumerable<IBankDataProvider>>().Single(p => p.ProviderId == "plaid");
        await provider.UnlinkAsync(connection.Id.ToString(), Ct);
        item.Removed.ShouldBeTrue();

        await host.Secrets.SetAsync(SecretKeys.Connection(connection.Id), $$"""{"v":1,"provider":"plaid","environment":"sandbox","accessToken":"{{item.AccessToken}}"}""");
        await provider.UnlinkAsync(connection.Id.ToString(), Ct);
        host.Secrets.Contains(SecretKeys.Connection(connection.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Credentials_report_presence_only()
    {
        await using var host = await SyncTestHost.CreateAsync(withKeys: false);
        var credentials = host.Get<IBankCredentialsService>();
        (await credentials.GetPlaidStatusAsync(Ct)).ShouldBe(new PlaidCredentialsStatus(false, false, PlaidEnvironment.Sandbox));

        await credentials.SetPlaidClientIdAsync(" id ", Ct);
        await credentials.SetPlaidSecretAsync("secret", Ct);
        await credentials.SetPlaidEnvironmentAsync(PlaidEnvironment.Production, Ct);
        var status = await credentials.GetPlaidStatusAsync(Ct);
        status.ShouldBe(new PlaidCredentialsStatus(true, true, PlaidEnvironment.Production));
        status.IsComplete.ShouldBeTrue();
        (await host.Secrets.GetAsync(SecretKeys.PlaidClientId)).ShouldBe("id");

        await credentials.SetPlaidSecretAsync("  ", Ct);
        (await credentials.GetPlaidStatusAsync(Ct)).HasSecret.ShouldBeFalse();
        (await credentials.DescribeStoreAsync(Ct)).Backend.ShouldBe(Keel.Application.Security.SecretStoreBackend.InMemory);
    }
}
