using System.Collections.Concurrent;
using Keel.Application.Accounts;
using Keel.Application.Categories;
using Keel.Application.Files;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Payees;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Keel.Infrastructure.Tests.Ledger;

/// <summary>Records published messages.</summary>
public sealed class RecordingBus : IMessageBus
{
    public ConcurrentQueue<object> Messages { get; } = new();

    public IReadOnlyList<LedgerChanged> LedgerChanges => Messages.OfType<LedgerChanged>().ToList();

    public void Publish<TMessage>(TMessage message)
        where TMessage : class => Messages.Enqueue(message);
}

/// <summary>The real infrastructure DI graph over a new budget file in a temp directory.</summary>
public sealed class LedgerTestHost : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServiceProvider _services;

    private LedgerTestHost()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IMessageBus>(Bus);
        services.AddKeelInfrastructure(new DataDirectory(_temp.Path));
        _services = services.BuildServiceProvider();
    }

    public RecordingBus Bus { get; } = new();

    public string FilePath => _temp.File("Ledger.keel");

    public IAccountService Accounts => Get<IAccountService>();

    public ITransactionService Transactions => Get<ITransactionService>();

    public IPayeeService Payees => Get<IPayeeService>();

    public ICategoryService Categories => Get<ICategoryService>();

    public IBalanceSnapshotService Snapshots => Get<IBalanceSnapshotService>();

    public IRegisterQuery Register => Get<IRegisterQuery>();

    public IUndoService Undo => Get<IUndoService>();

    public IDbContextFactory<KeelDbContext> Factory => Get<IDbContextFactory<KeelDbContext>>();

    public static async Task<LedgerTestHost> CreateAsync()
    {
        var host = new LedgerTestHost();
        await host.Get<IBudgetFileService>().OpenOrCreateAsync(host.FilePath, CancellationToken.None);
        return host;
    }

    public T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    public KeelDbContext Db() => Factory.CreateDbContext();

    public async Task<AccountDto> CheckingAsync(string name = "Checking", long opening = 100_000) =>
        await Accounts.CreateAccountAsync(new CreateAccountRequest(name, AccountType.Checking, "USD", new DateOnly(2026, 8, 1), opening), CancellationToken.None);

    public async Task<AccountDto> AccountAsync(string name, AccountType type, long opening = 0) =>
        await Accounts.CreateAccountAsync(new CreateAccountRequest(name, type, "USD", new DateOnly(2026, 8, 1), opening), CancellationToken.None);

    public async Task<Guid> CategoryAsync(string name, string group = "Everyday") =>
        (await Categories.CreateCategoryAsync(group, name, CancellationToken.None)).Id;

    public Task<TransactionDto> AddAsync(Guid account, long amount, string? payee = "Grocer", Guid? category = null, DateOnly? date = null, string? memo = null) =>
        Transactions.SaveAsync(new SaveTransactionRequest(null, account, date ?? new DateOnly(2026, 8, 10), amount, payee, category, memo), CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _temp.Dispose();
    }
}
