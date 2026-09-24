using Keel.Application.Accounts;
using Keel.Application.Files;
using Keel.Application.Messaging;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Platform;
using Keel.Infrastructure.Sync.Plaid;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Tests.Sync;

/// <summary>The real infrastructure DI graph with an in-memory secret store and the fake Plaid server.</summary>
public sealed class SyncTestHost : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServiceProvider _services;

    private SyncTestHost(Action<IServiceCollection>? configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Logs);
        services.AddSingleton<ILoggerFactory>(Logs);
        services.AddSingleton(typeof(ILogger<>), typeof(SinkLogger<>));
        services.AddSingleton<IMessageBus>(Bus);
        services.AddSingleton(Secrets);
        services.AddSingleton<ISecretStore>(Secrets);
        services.AddSingleton<ISecretStoreInfo>(Secrets);
        services.AddSingleton(new PlaidProviderOptions { PollInterval = TimeSpan.FromMilliseconds(5), LinkTimeout = TimeSpan.FromSeconds(20) });
        services.AddKeelInfrastructure(new DataDirectory(_temp.Path));
        services.AddHttpClient(GoingPlaidApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Plaid);
        services.Configure<HttpStandardResilienceOptions>(GoingPlaidApi.HttpClientName + "-standard", o => o.Retry.Delay = TimeSpan.FromMilliseconds(1));
        configure?.Invoke(services);
        _services = services.BuildServiceProvider();
    }

    public RecordingBus Bus { get; } = new();

    public LogSink Logs { get; } = new();

    public InMemorySecretStore Secrets { get; } = new();

    public FakePlaidServer Plaid { get; } = new();

    public ISyncService Sync => Get<ISyncService>();

    public string FilePath => _temp.File("Sync.keel");

    public static async Task<SyncTestHost> CreateAsync(bool withKeys = true, Action<IServiceCollection>? configure = null)
    {
        var host = new SyncTestHost(configure);
        await host.Get<IBudgetFileService>().OpenOrCreateAsync(host.FilePath, CancellationToken.None);
        if (withKeys)
        {
            var credentials = host.Get<IBankCredentialsService>();
            await credentials.SetPlaidClientIdAsync(FakePlaidServer.ClientId, CancellationToken.None);
            await credentials.SetPlaidSecretAsync(FakePlaidServer.Secret, CancellationToken.None);
            await credentials.SetPlaidEnvironmentAsync(PlaidEnvironment.Sandbox, CancellationToken.None);
        }

        return host;
    }

    public T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    public KeelDbContext Db() => Get<IDbContextFactory<KeelDbContext>>().CreateDbContext();

    /// <summary>Links a Plaid item through Hosted Link (the fake user finishes at once) and creates new accounts for all of it.</summary>
    public async Task<(SyncConnectionDto Connection, FakeItem Item)> LinkAsync(Action<FakeItem>? setup = null, Func<PendingAccount, AccountLinkChoice>? choose = null)
    {
        var session = await Sync.BeginLinkAsync("plaid", CancellationToken.None);
        var completing = Sync.CompleteLinkAsync(session, CancellationToken.None);
        var item = Plaid.CompleteLink(session.SessionToken)!;
        setup?.Invoke(item);
        var pending = await completing;
        var choices = pending.Accounts.Select(choose ?? (a => new AccountLinkChoice(a.Account.ProviderAccountId, AccountLinkAction.CreateNew, NewName: a.Account.Name, NewType: a.Account.SuggestedType))).ToList();
        var connection = await Sync.SaveLinkAsync(pending, choices, CancellationToken.None);
        return (connection, item);
    }

    public async Task<Guid> AccountIdAsync(string providerAccountId)
    {
        await using var db = Db();
        return await db.Accounts.Where(a => a.ProviderAccountId == providerAccountId).Select(a => a.Id).SingleAsync();
    }

    public async Task<List<Transaction>> RowsAsync(Guid accountId, bool includeDeleted = false)
    {
        await using var db = Db();
        var query = includeDeleted ? db.Transactions.IgnoreQueryFilters() : db.Transactions;
        return await query.AsNoTracking().Where(t => t.AccountId == accountId && t.Source != TransactionSource.System)
            .OrderBy(t => t.Date).ThenBy(t => t.Id).ToListAsync();
    }

    public async Task<SyncConnection> ConnectionAsync(Guid id)
    {
        await using var db = Db();
        return await db.SyncConnections.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    public Task<AccountDto?> AccountAsync(Guid id) => Get<IAccountService>().GetAccountAsync(id, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _temp.Dispose();
    }
}
