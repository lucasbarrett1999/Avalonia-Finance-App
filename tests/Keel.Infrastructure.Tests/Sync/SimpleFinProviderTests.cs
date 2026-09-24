using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Application.Sync;
using Keel.Domain;
using Keel.Infrastructure.Sync.SimpleFin;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Infrastructure.Tests.Sync;

/// <summary>SimpleFIN Bridge (F-TXN-3 P1): setup token claim, account polling, sync through the pipeline.</summary>
public sealed class SimpleFinProviderTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string ClaimUrl = "https://bridge.test/simplefin/claim/demo-token-123";

    private static string SetupToken => Convert.ToBase64String(Encoding.UTF8.GetBytes(ClaimUrl));

    [Fact]
    public void Setup_tokens_decode_to_https_claim_urls_only()
    {
        SimpleFinProvider.ClaimUrlOf(SetupToken)!.ToString().ShouldBe(ClaimUrl);
        SimpleFinProvider.ClaimUrlOf(Convert.ToBase64String("http://bridge.test/claim"u8)).ShouldBeNull();
        SimpleFinProvider.ClaimUrlOf("not base64!").ShouldBeNull();
        SimpleFinProvider.ClaimUrlOf(null).ShouldBeNull();
    }

    [Fact]
    public void Access_url_credentials_move_into_a_basic_header()
    {
        var (url, basic) = SimpleFinProvider.SplitAccessUrl(new Uri("https://user:p%40ss@bridge.test/simplefin"));
        url.ToString().ShouldBe("https://bridge.test/simplefin");
        Encoding.UTF8.GetString(Convert.FromBase64String(basic!)).ShouldBe("user:p@ss");
    }

    [Theory]
    [InlineData("Visa Signature", AccountType.CreditCard)]
    [InlineData("High Yield Savings", AccountType.Savings)]
    [InlineData("Everyday Checking", AccountType.Checking)]
    [InlineData("Auto Loan", AccountType.Loan)]
    public void Account_types_are_guessed_from_names(string name, AccountType expected) =>
        SimpleFinProvider.GuessType(name).ShouldBe(expected);

    [Fact]
    public async Task Claim_link_sync_and_unlink_through_the_sync_service()
    {
        var bridge = new FakeSimpleFinBridge();
        await using var host = await SyncTestHost.CreateAsync(withKeys: false, configure: s =>
            s.AddHttpClient(SimpleFinProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => bridge));
        await host.Get<IBankCredentialsService>().SetSimpleFinSetupTokenAsync(SetupToken, Ct);
        host.Sync.Providers.Select(p => p.ProviderId).ShouldBe(["plaid", "simplefin"]);

        var session = await host.Sync.BeginLinkAsync("simplefin", Ct);
        session.OpensBrowser.ShouldBeFalse();
        bridge.Claims.ShouldBe(1);
        (await host.Get<IBankCredentialsService>().HasSimpleFinSetupTokenAsync(Ct)).ShouldBeFalse();
        var pending = await host.Sync.CompleteLinkAsync(session, Ct);
        pending.InstitutionName.ShouldBe("Demo Credit Union");
        pending.Accounts.Select(a => (a.Account.ProviderAccountId, a.Account.SuggestedType, a.Balance!.Current))
            .ShouldBe([("ACT-1", AccountType.Checking, 123_456L), ("ACT-2", AccountType.CreditCard, -20_000L)]);
        var connection = await host.Sync.SaveLinkAsync(
            pending,
            pending.Accounts.Select(a => new AccountLinkChoice(a.Account.ProviderAccountId, AccountLinkAction.CreateNew, NewName: a.Account.Name, NewType: a.Account.SuggestedType)).ToList(),
            Ct);
        connection.Provider.ShouldBe(SyncProvider.SimpleFin);

        var first = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        first.Added.ShouldBe(3);
        var checking = await host.AccountIdAsync("ACT-1");
        var rows = await host.RowsAsync(checking);
        rows.Select(r => (r.ProviderTransactionId, r.Amount, r.Status)).ShouldBe(
            [("TRN-1", -4_250L, TransactionStatus.Cleared), ("TRN-2", 250_000L, TransactionStatus.Cleared)]);
        (await host.AccountAsync(checking))!.ClearedBalance.Amount.ShouldBe(123_456);
        bridge.LastQuery.ShouldContain("start-date=");

        // The overlap window re-reads rows; provider ids keep them single.
        var second = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        second.Added.ShouldBe(0);
        (await host.RowsAsync(checking)).Count.ShouldBe(2);

        // Credentials never leave the Basic header, and the logs hold neither the token nor the password.
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("demo:secret-pass"));
        bridge.AuthorizationHeaders.ShouldAllBe(h => h == expected);
        host.Logs.All.ShouldNotContain("secret-pass");
        host.Logs.All.ShouldNotContain("demo-token-123");

        bridge.Revoked = true;
        var broken = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        (broken.Status, broken.ErrorCode).ShouldBe((SyncStatus.NeedsReauth, SimpleFinErrorCodes.AccessRevoked));

        await host.Sync.UnlinkAsync(connection.Id, Ct);
        host.Secrets.Contains(SecretKeys.Connection(connection.Id)).ShouldBeFalse();
        (await host.RowsAsync(checking)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_claimed_token_is_reported_as_invalid()
    {
        var bridge = new FakeSimpleFinBridge { ClaimStatus = HttpStatusCode.Forbidden };
        await using var host = await SyncTestHost.CreateAsync(withKeys: false, configure: s =>
            s.AddHttpClient(SimpleFinProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => bridge));
        await host.Get<IBankCredentialsService>().SetSimpleFinSetupTokenAsync(SetupToken, Ct);
        (await Should.ThrowAsync<BankProviderException>(host.Sync.BeginLinkAsync("simplefin", Ct))).Code.ShouldBe(BankErrorCodes.InvalidSetupToken);

        await host.Get<IBankCredentialsService>().SetSimpleFinSetupTokenAsync(null, Ct);
        (await Should.ThrowAsync<BankProviderException>(host.Sync.BeginLinkAsync("simplefin", Ct))).Code.ShouldBe(BankErrorCodes.MissingCredentials);
    }

    /// <summary>A SimpleFIN Bridge: claim endpoint and <c>/accounts</c> behind Basic auth.</summary>
    private sealed class FakeSimpleFinBridge : HttpMessageHandler
    {
        public int Claims { get; private set; }

        public HttpStatusCode ClaimStatus { get; set; } = HttpStatusCode.OK;

        public bool Revoked { get; set; }

        public string LastQuery { get; private set; } = string.Empty;

        public List<string> AuthorizationHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Post && uri.ToString() == ClaimUrl)
            {
                Claims++;
                return Task.FromResult(new HttpResponseMessage(ClaimStatus) { Content = new StringContent("https://demo:secret-pass@bridge.test/simplefin") });
            }

            uri.UserInfo.ShouldBeEmpty();
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            if (Revoked)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            LastQuery = uri.Query;
            var balancesOnly = uri.Query.Contains("balances-only=1", StringComparison.Ordinal);
            var org = new JsonObject { ["domain"] = "democu.test", ["name"] = "Demo Credit Union" };
            JsonArray Transactions(params (string Id, long Posted, string Amount, string Description)[] rows) =>
                balancesOnly ? [] : new JsonArray(rows.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["posted"] = r.Posted, ["amount"] = r.Amount, ["description"] = r.Description }).ToArray());
            var body = new JsonObject
            {
                ["errors"] = new JsonArray(),
                ["accounts"] = new JsonArray(
                    new JsonObject
                    {
                        ["org"] = org.DeepClone(),
                        ["id"] = "ACT-1",
                        ["name"] = "Everyday Checking",
                        ["currency"] = "USD",
                        ["balance"] = "1234.56",
                        ["available-balance"] = "1200.00",
                        ["balance-date"] = 1_790_000_000,
                        ["transactions"] = Transactions(("TRN-1", 1_789_500_000, "-42.50", "COFFEE SHOP"), ("TRN-2", 1_789_600_000, "2500.00", "PAYROLL")),
                    },
                    new JsonObject
                    {
                        ["org"] = org.DeepClone(),
                        ["id"] = "ACT-2",
                        ["name"] = "Visa Card",
                        ["currency"] = "USD",
                        ["balance"] = "-200.00",
                        ["balance-date"] = 1_790_000_000,
                        ["transactions"] = Transactions(("TRN-3", 1_789_700_000, "-200.00", "HARDWARE STORE")),
                    }),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }
}
