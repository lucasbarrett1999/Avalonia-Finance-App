using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Keel.Infrastructure.Tests.Sync;

/// <summary>
/// An in-memory Plaid API behind an <see cref="HttpMessageHandler"/>: link tokens and Hosted Link
/// sessions, public token exchange, items with accounts and a change log served by
/// <c>/transactions/sync</c> cursors, item errors (<c>ITEM_LOGIN_REQUIRED</c>), <c>/item/remove</c>,
/// and injected transient failures. The real Going.Plaid client and resilience pipeline run on top.
/// </summary>
public sealed class FakePlaidServer : HttpMessageHandler
{
    public const string ClientId = "test-client-id";
    public const string Secret = "test-secret-value";

    private readonly ConcurrentDictionary<string, LinkState> _links = new();
    private readonly ConcurrentDictionary<string, FakeItem> _itemsByToken = new();
    private readonly ConcurrentDictionary<string, FakeItem> _itemsByPublicToken = new();
    private readonly Lock _lock = new();
    private int _counter;

    /// <summary>Paths requested, in order.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Request bodies, in order (for credential checks).</summary>
    public ConcurrentQueue<JsonObject> Bodies { get; } = new();

    /// <summary>Number of upcoming requests answered with HTTP 500 (to exercise retries).</summary>
    public int TransientFailures;

    /// <summary>When set, the next <c>/transactions/sync</c> call with a non-initial cursor fails with a pagination mutation.</summary>
    public bool FailNextPageWithMutation { get; set; }

    /// <summary>When set, <c>/transactions/sync</c> from this change-log position on fails with <c>ITEM_LOGIN_REQUIRED</c>.</summary>
    public int? LoginRequiredFromPosition { get; set; }

    /// <summary>Items created so far.</summary>
    public IReadOnlyCollection<FakeItem> Items => _itemsByToken.Values.ToList();

    /// <summary>The latest link token handed out.</summary>
    public string? LastLinkToken { get; private set; }

    /// <summary>An item template used when a link completes: institution and accounts.</summary>
    public Func<FakeItem> NewItem { get; set; } = FakeItem.Default;

    /// <summary>Simulates the user finishing Hosted Link in the browser.</summary>
    public FakeItem? CompleteLink(string? linkToken = null)
    {
        var link = _links[linkToken ?? LastLinkToken!];
        if (link.UpdateAccessToken is { } access)
        {
            _itemsByToken[access].LoginRequired = false;
            link.Finished = true;
            return _itemsByToken[access];
        }

        var item = NewItem();
        item.ItemId = "item-" + Next();
        item.AccessToken = "access-sandbox-" + Next();
        item.PublicToken = "public-sandbox-" + Next();
        _itemsByPublicToken[item.PublicToken] = item;
        link.PublicToken = item.PublicToken;
        link.Institution = item.InstitutionName;
        link.Finished = true;
        return item;
    }

    /// <summary>Simulates the user closing Hosted Link without linking.</summary>
    public void ExitLink(string? linkToken = null)
    {
        var link = _links[linkToken ?? LastLinkToken!];
        link.Exited = true;
        link.Finished = true;
    }

    /// <summary>Makes an item available for exchange without a link (like <c>/sandbox/public_token/create</c>).</summary>
    public FakeItem ItemByAccessToken(string accessToken) => _itemsByToken[accessToken];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Enqueue(path);
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        Bodies.Enqueue(body);
        if (Interlocked.Decrement(ref TransientFailures) >= 0)
        {
            return Error(HttpStatusCode.InternalServerError, "API_ERROR", "INTERNAL_SERVER_ERROR");
        }

        Interlocked.Exchange(ref TransientFailures, 0);
        if ((string?)body["client_id"] != ClientId || (string?)body["secret"] != Secret)
        {
            return Error(HttpStatusCode.BadRequest, "INVALID_INPUT", "INVALID_API_KEYS");
        }

        lock (_lock)
        {
            return path switch
            {
                "/link/token/create" => LinkTokenCreate(body),
                "/link/token/get" => LinkTokenGet(body),
                "/item/public_token/exchange" => Exchange(body),
                "/sandbox/public_token/create" => SandboxPublicToken(body),
                "/sandbox/item/reset_login" => WithItem(body, item => { item.LoginRequired = true; return Ok(new JsonObject { ["reset_login"] = true }); }),
                "/accounts/get" => WithItem(body, AccountsGet, requireHealthy: true),
                "/transactions/sync" => WithItem(body, item => TransactionsSync(item, body), requireHealthy: true),
                "/item/get" => WithItem(body, ItemGet),
                "/item/remove" => WithItem(body, item => { item.Removed = true; _itemsByToken.TryRemove(item.AccessToken, out _); return Ok([]); }),
                _ => Error(HttpStatusCode.NotFound, "INVALID_REQUEST", "UNKNOWN_FIELDS"),
            };
        }
    }

    private HttpResponseMessage LinkTokenCreate(JsonObject body)
    {
        var access = (string?)body["access_token"];
        if (access is not null && !_itemsByToken.ContainsKey(access))
        {
            return Error(HttpStatusCode.BadRequest, "INVALID_INPUT", "INVALID_ACCESS_TOKEN");
        }

        if (body["hosted_link"] is null)
        {
            return Error(HttpStatusCode.BadRequest, "INVALID_REQUEST", "MISSING_FIELDS");
        }

        var token = "link-sandbox-" + Next();
        _links[token] = new LinkState { UpdateAccessToken = access, HasProducts = body["products"] is not null };
        LastLinkToken = token;
        return Ok(new JsonObject
        {
            ["link_token"] = token,
            ["expiration"] = DateTimeOffset.UtcNow.AddHours(4).ToString("O", CultureInfo.InvariantCulture),
            ["hosted_link_url"] = "https://hosted.plaid.com/link/" + token,
        });
    }

    private HttpResponseMessage LinkTokenGet(JsonObject body)
    {
        var token = (string)body["link_token"]!;
        if (!_links.TryGetValue(token, out var link))
        {
            return Error(HttpStatusCode.BadRequest, "INVALID_INPUT", "INVALID_LINK_TOKEN");
        }

        var sessions = new JsonArray();
        if (link.Finished)
        {
            var session = new JsonObject
            {
                ["link_session_id"] = "session-" + token,
                ["started_at"] = "2026-09-24T12:00:00Z",
                ["finished_at"] = "2026-09-24T12:01:00Z",
            };
            if (link.Exited)
            {
                session["exit"] = new JsonObject { ["error"] = null, ["metadata"] = new JsonObject { ["status"] = "requires_credentials" } };
            }
            else if (link.PublicToken is not null)
            {
                session["results"] = new JsonObject
                {
                    ["item_add_results"] = new JsonArray(new JsonObject
                    {
                        ["public_token"] = link.PublicToken,
                        ["institution"] = new JsonObject { ["name"] = link.Institution, ["institution_id"] = "ins_109508" },
                        ["accounts"] = new JsonArray(),
                    }),
                };
            }

            sessions.Add(session);
        }

        return Ok(new JsonObject { ["link_token"] = token, ["link_sessions"] = sessions });
    }

    private HttpResponseMessage Exchange(JsonObject body)
    {
        var publicToken = (string)body["public_token"]!;
        if (!_itemsByPublicToken.TryRemove(publicToken, out var item))
        {
            return Error(HttpStatusCode.BadRequest, "INVALID_INPUT", "INVALID_PUBLIC_TOKEN");
        }

        _itemsByToken[item.AccessToken] = item;
        return Ok(new JsonObject { ["access_token"] = item.AccessToken, ["item_id"] = item.ItemId });
    }

    private HttpResponseMessage SandboxPublicToken(JsonObject body)
    {
        var item = NewItem();
        item.ItemId = "item-" + Next();
        item.AccessToken = "access-sandbox-" + Next();
        item.PublicToken = "public-sandbox-" + Next();
        _itemsByPublicToken[item.PublicToken] = item;
        return Ok(new JsonObject { ["public_token"] = item.PublicToken });
    }

    private HttpResponseMessage WithItem(JsonObject body, Func<FakeItem, HttpResponseMessage> handler, bool requireHealthy = false)
    {
        if ((string?)body["access_token"] is not { } access || !_itemsByToken.TryGetValue(access, out var item))
        {
            return Error(HttpStatusCode.BadRequest, "INVALID_INPUT", "INVALID_ACCESS_TOKEN");
        }

        return requireHealthy && item.LoginRequired
            ? Error(HttpStatusCode.BadRequest, "ITEM_ERROR", "ITEM_LOGIN_REQUIRED")
            : handler(item);
    }

    private static HttpResponseMessage AccountsGet(FakeItem item) => Ok(new JsonObject
    {
        ["accounts"] = new JsonArray(item.Accounts.Select(a => (JsonNode)a.ToJson()).ToArray()),
        ["item"] = new JsonObject { ["item_id"] = item.ItemId, ["institution_name"] = item.InstitutionName },
    });

    private static HttpResponseMessage ItemGet(FakeItem item) => Ok(new JsonObject
    {
        ["item"] = new JsonObject
        {
            ["item_id"] = item.ItemId,
            ["institution_name"] = item.InstitutionName,
            ["error"] = item.LoginRequired
                ? new JsonObject { ["error_type"] = "ITEM_ERROR", ["error_code"] = "ITEM_LOGIN_REQUIRED", ["error_message"] = "the login details of this item have changed" }
                : null,
        },
    });

    private HttpResponseMessage TransactionsSync(FakeItem item, JsonObject body)
    {
        var cursor = (string?)body["cursor"];
        var start = string.IsNullOrEmpty(cursor) ? 0 : int.Parse(cursor[1..], CultureInfo.InvariantCulture);
        if (start > 0 && FailNextPageWithMutation)
        {
            FailNextPageWithMutation = false;
            return Error(HttpStatusCode.BadRequest, "TRANSACTIONS_ERROR", "TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION");
        }

        if (start >= LoginRequiredFromPosition)
        {
            return Error(HttpStatusCode.BadRequest, "ITEM_ERROR", "ITEM_LOGIN_REQUIRED");
        }

        var count = (int?)body["count"] ?? 100;
        var events = item.Events.Skip(start).Take(count).ToList();
        var next = start + events.Count;
        var added = new JsonArray();
        var modified = new JsonArray();
        var removed = new JsonArray();
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case FakeEventKind.Added:
                    added.Add(e.Transaction!.ToJson());
                    break;
                case FakeEventKind.Modified:
                    modified.Add(e.Transaction!.ToJson());
                    break;
                default:
                    removed.Add(new JsonObject { ["transaction_id"] = e.RemovedId, ["account_id"] = e.AccountId });
                    break;
            }
        }

        return Ok(new JsonObject
        {
            ["accounts"] = new JsonArray(item.Accounts.Select(a => (JsonNode)a.ToJson()).ToArray()),
            ["added"] = added,
            ["modified"] = modified,
            ["removed"] = removed,
            ["next_cursor"] = "c" + next.ToString(CultureInfo.InvariantCulture),
            ["has_more"] = next < item.Events.Count,
            ["transactions_update_status"] = item.HistoryComplete ? "HISTORICAL_UPDATE_COMPLETE" : "INITIAL_UPDATE_COMPLETE",
        });
    }

    private string Next() => Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture);

    private static HttpResponseMessage Ok(JsonObject body)
    {
        body["request_id"] = "req-" + Guid.NewGuid().ToString("N")[..8];
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string type, string code) =>
        new(status)
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["error_type"] = type,
                    ["error_code"] = code,
                    ["error_message"] = "fake " + code,
                    ["display_message"] = null,
                    ["request_id"] = "req-error",
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class LinkState
    {
        public string? UpdateAccessToken { get; init; }

        public bool HasProducts { get; init; }

        public string? PublicToken { get; set; }

        public string? Institution { get; set; }

        public bool Finished { get; set; }

        public bool Exited { get; set; }
    }
}

public enum FakeEventKind
{
    Added,
    Modified,
    Removed,
}

public sealed record FakeEvent(FakeEventKind Kind, FakeTransaction? Transaction, string? RemovedId, string AccountId);

/// <summary>A Plaid item: institution, accounts, and a change log that cursors walk.</summary>
public sealed class FakeItem
{
    public string ItemId { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string PublicToken { get; set; } = string.Empty;

    public string InstitutionName { get; set; } = "First Platypus Bank";

    public List<FakeAccount> Accounts { get; } = [];

    public List<FakeEvent> Events { get; } = [];

    public bool LoginRequired { get; set; }

    public bool Removed { get; set; }

    public bool HistoryComplete { get; set; } = true;

    public static FakeItem Default()
    {
        var item = new FakeItem();
        item.Accounts.Add(new FakeAccount("acc-checking", "Plaid Checking", "0000", "depository", "checking", 110.00m, 100.00m));
        item.Accounts.Add(new FakeAccount("acc-credit", "Plaid Credit Card", "3333", "credit", "credit card", 410.00m, null));
        return item;
    }

    public FakeTransaction Add(string id, string accountId, DateOnly date, decimal plaidAmount, string name, string? merchant = null, bool pending = false, string? pendingId = null)
    {
        var t = new FakeTransaction(id, accountId, date, plaidAmount, name, merchant, pending, pendingId);
        Events.Add(new FakeEvent(FakeEventKind.Added, t, null, accountId));
        return t;
    }

    public void Modify(FakeTransaction transaction) => Events.Add(new FakeEvent(FakeEventKind.Modified, transaction, null, transaction.AccountId));

    public void Remove(string id, string accountId) => Events.Add(new FakeEvent(FakeEventKind.Removed, null, id, accountId));
}

public sealed record FakeAccount(string Id, string Name, string Mask, string Type, string Subtype, decimal Current, decimal? Available)
{
    public decimal Current { get; set; } = Current;

    public JsonObject ToJson() => new()
    {
        ["account_id"] = Id,
        ["name"] = Name,
        ["official_name"] = Name + " Official",
        ["mask"] = Mask,
        ["type"] = Type,
        ["subtype"] = Subtype,
        ["balances"] = new JsonObject
        {
            ["current"] = Current,
            ["available"] = Available,
            ["iso_currency_code"] = "USD",
            ["limit"] = null,
        },
    };
}

public sealed record FakeTransaction(string Id, string AccountId, DateOnly Date, decimal Amount, string Name, string? Merchant, bool Pending, string? PendingId)
{
    public JsonObject ToJson() => new()
    {
        ["transaction_id"] = Id,
        ["account_id"] = AccountId,
        ["amount"] = Amount,
        ["iso_currency_code"] = "USD",
        ["date"] = Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["name"] = Name,
        ["merchant_name"] = Merchant,
        ["pending"] = Pending,
        ["pending_transaction_id"] = PendingId,
        ["payment_channel"] = "in store",
    };
}
