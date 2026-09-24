using Keel.Application.Sync;
using Keel.Desktop.ViewModels.Sync;
using Keel.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Keel.Desktop.Tests;

/// <summary>
/// A bank provider for UI tests: the "browser" link finishes when the test calls <see cref="FinishLink"/>,
/// accounts and transactions are fixed, and <see cref="NeedsReauth"/> simulates ITEM_LOGIN_REQUIRED.
/// </summary>
internal sealed class FakeBankProvider : IBankDataProvider
{
    private TaskCompletionSource _link = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _cursor;

    public string ProviderId => "plaid";

    public bool NeedsReauth { get; set; }

    public int LinksStarted { get; private set; }

    public bool LinkCancelled { get; private set; }

    public List<string> Unlinked { get; } = [];

    public List<ProviderTransaction> Transactions { get; } =
    [
        new("t1", "acc-checking", new DateOnly(2026, 9, 10), -1_234, "Blue Bottle Coffee", null, false, null),
        new("t2", "acc-checking", new DateOnly(2026, 9, 11), 150_000, "Acme Payroll", null, false, null),
        new("t3", "acc-card", new DateOnly(2026, 9, 12), -5_510, "Trader Joe's", null, false, null),
    ];

    /// <summary>Registers this fake as the only bank provider, with Plaid keys in the in-memory secret store.</summary>
    public static Action<IServiceCollection> Install(FakeBankProvider fake) => services =>
    {
        services.RemoveAll<IBankDataProvider>();
        services.AddSingleton<IBankDataProvider>(fake);
    };

    public void FinishLink() => _link.TrySetResult();

    public Task<LinkSession> BeginLinkAsync(LinkMode mode, string? existingConnectionId, CancellationToken ct)
    {
        LinksStarted++;
        _link = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return Task.FromResult(new LinkSession(ProviderId, "link-sandbox-test", new Uri("https://hosted.plaid.com/link/test"), DateTimeOffset.UtcNow.AddHours(4), existingConnectionId));
    }

    public async Task<LinkResult> CompleteLinkAsync(LinkSession session, CancellationToken ct)
    {
        try
        {
            await _link.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            LinkCancelled = true;
            throw;
        }

        if (session.ExistingConnectionId is { } existing)
        {
            NeedsReauth = false;
            return new LinkResult(true, existing, null, null);
        }

        return new LinkResult(true, Guid.CreateVersion7().ToString(), "First Platypus Bank", null) { ExternalItemId = "item-1" };
    }

    public Task<IReadOnlyList<ProviderAccount>> ListAccountsAsync(string connectionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderAccount>>(
        [
            new("acc-checking", "Plaid Checking", "0000", AccountType.Checking, "USD"),
            new("acc-card", "Plaid Credit Card", "3333", AccountType.CreditCard, "USD"),
        ]);

    public Task<SyncResult> SyncTransactionsAsync(string connectionId, string? cursor, CancellationToken ct)
    {
        ThrowIfBroken();
        var start = int.TryParse(cursor, out var c) ? c : 0;
        var page = Transactions.Skip(start).ToList();
        _cursor = start + page.Count;
        return Task.FromResult(new SyncResult(page, [], [], _cursor.ToString(System.Globalization.CultureInfo.InvariantCulture), false));
    }

    public Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(string connectionId, CancellationToken ct)
    {
        ThrowIfBroken();
        return Task.FromResult<IReadOnlyList<ProviderBalance>>(
        [
            new("acc-checking", 148_766, 148_766, "USD", DateTimeOffset.UtcNow),
            new("acc-card", -5_510, null, "USD", DateTimeOffset.UtcNow),
        ]);
    }

    public Task<ConnectionHealth> GetHealthAsync(string connectionId, CancellationToken ct) =>
        Task.FromResult(new ConnectionHealth(NeedsReauth ? SyncStatus.NeedsReauth : SyncStatus.Ok, null, DateTimeOffset.UtcNow));

    public Task UnlinkAsync(string connectionId, CancellationToken ct)
    {
        Unlinked.Add(connectionId);
        return Task.CompletedTask;
    }

    private void ThrowIfBroken()
    {
        if (NeedsReauth)
        {
            throw new BankProviderException(SyncStatus.NeedsReauth, BankErrorCodes.LoginRequired, "login required");
        }
    }
}

/// <summary>Records the URLs the app asks the browser to open.</summary>
internal sealed class FakeBrowser(bool succeeds = true) : IBrowserLauncher
{
    public List<Uri> Opened { get; } = [];

    public Task<bool> OpenAsync(Uri url)
    {
        Opened.Add(url);
        return Task.FromResult(succeeds);
    }
}
