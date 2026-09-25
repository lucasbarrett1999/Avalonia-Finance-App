using System.Collections.Concurrent;
using Keel.Application.Accounts;
using Keel.Application.Files;
using Keel.Application.Messaging;
using Keel.Application.Security;
using Keel.Domain;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Platform;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Tests.Encryption;

/// <summary>
/// App runs and sessions over one data directory, like the desktop app: every session is a new DI container
/// (ADR 0080) sharing one in-memory secret store and, within one app run, one <see cref="BudgetFileKeyRing"/>.
/// All log lines are captured so tests can check that no key or passphrase is ever written.
/// </summary>
public sealed class EncryptionTestHost : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<ServiceProvider> _sessions = [];

    public EncryptionTestHost()
    {
        Data = new DataDirectory(_temp.Path);
        Data.EnsureCreated();
    }

    public DataDirectory Data { get; }

    public InMemorySecretStore Secrets { get; } = new();

    public BudgetFileKeyRing KeyRing { get; private set; } = new();

    public ConcurrentQueue<string> Logs { get; } = new();

    public string FilePath => Path.Combine(Data.BudgetsDirectory, "Home.keel");

    /// <summary>A new app run: the key ring of the previous run is gone; the secret store stays.</summary>
    public void Restart() => KeyRing = new BudgetFileKeyRing();

    /// <summary>A new session container (nothing opened yet).</summary>
    public ServiceProvider NewSession()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(new CapturingLoggerFactory(Logs));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IMessageBus>(new RecordingBus());
        services.AddSingleton<ISecretStore>(Secrets);
        services.AddSingleton<ISecretStoreInfo>(Secrets);
        services.AddKeelInfrastructure(Data);
        services.AddSingleton(KeyRing);
        var provider = services.BuildServiceProvider();
        _sessions.Add(provider);
        return provider;
    }

    /// <summary>A new session with <see cref="FilePath"/> open.</summary>
    public async Task<ServiceProvider> OpenAsync(BudgetFileUnlock? unlock = null)
    {
        var session = NewSession();
        await session.GetRequiredService<IBudgetFileService>().OpenOrCreateAsync(FilePath, unlock, CancellationToken.None);
        return session;
    }

    /// <summary>Closes every session (the file is then free to convert).</summary>
    public async Task CloseAllAsync()
    {
        foreach (var session in _sessions)
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Converts the file the way the desktop does: every session closed, then a fresh one converts.</summary>
    public async Task ConvertAsync(BudgetFileEncryptionChange change)
    {
        await CloseAllAsync();
        var session = NewSession();
        await session.GetRequiredService<IBudgetFileEncryption>().ConvertAsync(FilePath, change, CancellationToken.None);
    }

    public static async Task<AccountDto> AccountAsync(IServiceProvider session, string name, long opening = 50_000) =>
        await session.GetRequiredService<IAccountService>().CreateAccountAsync(new CreateAccountRequest(name, AccountType.Checking, "USD", new DateOnly(2026, 8, 1), opening), CancellationToken.None);

    public static async Task<List<string>> AccountNamesAsync(IServiceProvider session)
    {
        await using var db = await session.GetRequiredService<IDbContextFactory<KeelDbContext>>().CreateDbContextAsync();
        return await db.Accounts.OrderBy(a => a.Name).Select(a => a.Name).ToListAsync();
    }

    /// <summary>Whether a file starts with the plain SQLite header.</summary>
    public static bool IsPlain(string path) => !SqlCipher.IsEncrypted(path);

    public async ValueTask DisposeAsync()
    {
        await CloseAllAsync();
        _temp.Dispose();
    }

    private sealed class CapturingLoggerFactory(ConcurrentQueue<string> lines) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(lines);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lines.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
        }
    }
}
