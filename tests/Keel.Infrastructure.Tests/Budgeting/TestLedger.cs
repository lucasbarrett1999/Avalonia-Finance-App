using System.Globalization;
using Keel.Application.Messaging;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Budgeting;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Keel.Infrastructure.Tests.Budgeting;

/// <summary>A migrated budget file in a temp directory plus helpers to write a hand-built ledger.</summary>
internal sealed class TestLedger : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<object> _pending = [];
    private int _sort;

    private TestLedger()
    {
    }

    public KeelDbContextFactory Factory { get; } = new();

    public RecordingBus Bus { get; } = new();

    public FixedTimeProvider Time { get; } = new(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));

    public Dictionary<Guid, Guid> PaymentCategories { get; } = [];

    public static Guid Rta => SystemIds.ReadyToAssignCategory;

    public Guid EverydayGroup { get; } = EntityIds.New();

    public static async Task<TestLedger> CreateAsync()
    {
        var ledger = new TestLedger();
        var files = new BudgetFileService(ledger.Factory, NullLogger<BudgetFileService>.Instance);
        await files.OpenOrCreateAsync(ledger._temp.File("Budget.keel"), CancellationToken.None);
        ledger._pending.Add(new CategoryGroup { Id = ledger.EverydayGroup, Name = "Everyday", SortOrder = 2 });
        return ledger;
    }

    public BudgetService Service() => new(Factory, Bus, Time);

    public static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly M(string isoMonth) => D(isoMonth + "-01");

    public Guid Account(string name, AccountType type, bool closed = false)
    {
        var account = Keel.Domain.Entities.Account.Create(name, type, D("2026-01-01"));
        account.IsClosed = closed;
        account.SortOrder = _sort++;
        _pending.Add(account);
        if (account.IsOnBudget && AccountTypeInfo.IsCredit(type))
        {
            var pay = new Category
            {
                GroupId = SystemIds.CreditCardPaymentsGroup,
                Name = "Pay_" + name,
                IsSystem = true,
                LinkedAccountId = account.Id,
                SortOrder = PaymentCategories.Count,
            };
            _pending.Add(pay);
            PaymentCategories[account.Id] = pay.Id;
        }

        return account.Id;
    }

    public Guid Category(string name, Guid? group = null, bool hidden = false)
    {
        var category = new Category { GroupId = group ?? EverydayGroup, Name = name, SortOrder = _sort++, IsHidden = hidden };
        _pending.Add(category);
        return category.Id;
    }

    public Transaction Txn(string date, Guid account, long amount, Guid? category, bool deleted = false)
    {
        var txn = new Transaction { AccountId = account, Date = D(date), Amount = amount, CategoryId = category, IsDeleted = deleted, IsApproved = true };
        _pending.Add(txn);
        return txn;
    }

    public (Transaction From, Transaction To) Transfer(string date, Guid from, Guid to, long amount, Guid? fromCategory = null, Guid? toCategory = null)
    {
        var pair = EntityIds.New();
        var a = new Transaction { AccountId = from, Date = D(date), Amount = -amount, CategoryId = fromCategory, TransferAccountId = to, TransferPairId = pair };
        var b = new Transaction { AccountId = to, Date = D(date), Amount = amount, CategoryId = toCategory, TransferAccountId = from, TransferPairId = pair };
        _pending.Add(a);
        _pending.Add(b);
        return (a, b);
    }

    public Transaction Split(string date, Guid account, bool deleted, params (Guid? Category, Guid? TransferAccount, long Amount)[] splits)
    {
        var txn = new Transaction { AccountId = account, Date = D(date), Amount = splits.Sum(s => s.Amount), IsDeleted = deleted };
        foreach (var (category, transfer, amount) in splits)
        {
            txn.Splits.Add(new TransactionSplit { CategoryId = category, TransferAccountId = transfer, Amount = amount });
        }

        _pending.Add(txn);
        return txn;
    }

    public void Assignment(Guid category, string month, long amount) =>
        _pending.Add(new BudgetAssignment { CategoryId = category, Month = M(month), Assigned = amount });

    public async Task SaveAsync()
    {
        await using var db = Factory.CreateDbContext();
        foreach (var entity in _pending)
        {
            db.Add(entity);
        }

        await db.SaveChangesAsync();
        _pending.Clear();
    }

    public void Dispose() => _temp.Dispose();
}

internal sealed class RecordingBus : IMessageBus
{
    public List<object> Messages { get; } = [];

    public void Publish<TMessage>(TMessage message)
        where TMessage : class => Messages.Add(message);
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
