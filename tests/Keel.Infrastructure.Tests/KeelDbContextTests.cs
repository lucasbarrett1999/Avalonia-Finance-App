using Keel.Application.Files;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Keel.Infrastructure.Tests;

public sealed class KeelDbContextTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly KeelDbContextFactory _factory = new();

    public void Dispose() => _temp.Dispose();

    private async Task<BudgetFileInfo> OpenAsync(string name = "Test.keel")
    {
        var service = new BudgetFileService(_factory, NullLogger<BudgetFileService>.Instance);
        return await service.OpenOrCreateAsync(_temp.File(name), CancellationToken.None);
    }

    private static Account NewChecking() => Account.Create("Checking", AccountType.Checking, new DateOnly(2026, 8, 1));

    [Fact]
    public async Task Creates_database_via_migrations()
    {
        var info = await OpenAsync();

        info.Created.ShouldBeTrue();
        info.AppliedMigrations.ShouldNotBeEmpty();
        File.Exists(info.Path).ShouldBeTrue();
        _factory.CurrentPath.ShouldBe(info.Path);

        await using var db = _factory.CreateDbContext();
        (await db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Reopening_applies_nothing()
    {
        await OpenAsync();
        var second = await OpenAsync();

        second.Created.ShouldBeFalse();
        second.AppliedMigrations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Model_has_no_changes_missing_from_migrations()
    {
        await OpenAsync();
        await using var db = _factory.CreateDbContext();
        db.Database.HasPendingModelChanges().ShouldBeFalse("run `dotnet ef migrations add` after changing the model");
    }

    [Fact]
    public async Task Seeds_system_rows()
    {
        await OpenAsync();
        await using var db = _factory.CreateDbContext();

        var profile = await db.Profiles.SingleAsync();
        profile.Id.ShouldBe(SystemIds.DefaultProfile);
        profile.IsDefault.ShouldBeTrue();

        var groups = await db.CategoryGroups.OrderBy(g => g.SortOrder).ToListAsync();
        groups.Select(g => g.Name).ShouldBe(["Inflow", "Credit Card Payments"]);
        groups.ShouldAllBe(g => g.IsSystem);

        var rta = await db.Categories.SingleAsync(c => c.Id == SystemIds.ReadyToAssignCategory);
        rta.Name.ShouldBe("Ready to Assign");
        rta.GroupId.ShouldBe(SystemIds.InflowGroup);
    }

    [Fact]
    public async Task Round_trips_an_account_and_a_transaction()
    {
        await OpenAsync();
        var account = NewChecking();
        account.Notes = "Main account";
        account.ReportedBalance = 123_45;
        account.ReportedBalanceAt = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
        var created = new DateTime(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc);
        var txn = new Transaction
        {
            AccountId = account.Id,
            Date = new DateOnly(2026, 8, 1),
            PayeeRaw = "Employer Inc",
            Memo = "Paycheck",
            Amount = 3_000_00,
            CategoryId = SystemIds.ReadyToAssignCategory,
            Status = TransactionStatus.Cleared,
            IsApproved = true,
            Source = TransactionSource.Manual,
            ImportFingerprint = new string('a', 64),
            CreatedAt = created,
            UpdatedAt = created,
        };

        await using (var db = _factory.CreateDbContext())
        {
            db.Accounts.Add(account);
            db.Transactions.Add(txn);
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            var loadedAccount = await db.Accounts.SingleAsync(a => a.Id == account.Id);
            loadedAccount.Name.ShouldBe("Checking");
            loadedAccount.Type.ShouldBe(AccountType.Checking);
            loadedAccount.Currency.ShouldBe("USD");
            loadedAccount.IsOnBudget.ShouldBeTrue();
            loadedAccount.OpeningDate.ShouldBe(new DateOnly(2026, 8, 1));
            loadedAccount.Notes.ShouldBe("Main account");
            loadedAccount.ReportedBalance.ShouldBe(123_45);
            loadedAccount.ReportedBalanceAt.ShouldBe(account.ReportedBalanceAt);
            loadedAccount.ReportedBalanceAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);

            var loaded = await db.Transactions.SingleAsync(t => t.Id == txn.Id);
            loaded.AccountId.ShouldBe(account.Id);
            loaded.Date.ShouldBe(new DateOnly(2026, 8, 1));
            loaded.PayeeRaw.ShouldBe("Employer Inc");
            loaded.Memo.ShouldBe("Paycheck");
            loaded.Amount.ShouldBe(3_000_00);
            loaded.CategoryId.ShouldBe(SystemIds.ReadyToAssignCategory);
            loaded.Status.ShouldBe(TransactionStatus.Cleared);
            loaded.Source.ShouldBe(TransactionSource.Manual);
            loaded.IsApproved.ShouldBeTrue();
            loaded.CreatedAt.ShouldBe(created);
            loaded.CreatedAt.Kind.ShouldBe(DateTimeKind.Utc);

            // Queries on DateOnly work against the ISO text column.
            (await db.Transactions.CountAsync(t => t.Date >= new DateOnly(2026, 8, 1) && t.Date < new DateOnly(2026, 9, 1))).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Stores_dates_as_iso_text_amounts_as_integers_and_enums_by_name()
    {
        await OpenAsync();
        var account = NewChecking();
        await using (var db = _factory.CreateDbContext())
        {
            db.Accounts.Add(account);
            db.Transactions.Add(new Transaction { AccountId = account.Id, Date = new DateOnly(2026, 8, 5), PayeeRaw = "Rent", Amount = -1_500_00 });
            await db.SaveChangesAsync();
        }

        await using var connection = new SqliteConnection(KeelDatabase.ConnectionString(_factory.CurrentPath!));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "Date", typeof("Date"), "Amount", typeof("Amount"), "Status" FROM "Transactions" """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetString(0).ShouldBe("2026-08-05");
        reader.GetString(1).ShouldBe("text");
        reader.GetInt64(2).ShouldBe(-1_500_00);
        reader.GetString(3).ShouldBe("integer");
        reader.GetString(4).ShouldBe("Uncleared");
    }

    [Fact]
    public async Task Opens_connections_in_wal_mode_with_foreign_keys()
    {
        await OpenAsync();
        await using var db = _factory.CreateDbContext();
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();

        (await ScalarAsync(connection, "PRAGMA journal_mode;")).ShouldBe("wal");
        (await ScalarAsync(connection, "PRAGMA foreign_keys;")).ShouldBe("1");
        (await ScalarAsync(connection, "PRAGMA synchronous;")).ShouldBe("1"); // NORMAL
    }

    [Fact]
    public async Task Enforces_foreign_keys()
    {
        await OpenAsync();
        await using var db = _factory.CreateDbContext();
        db.Transactions.Add(new Transaction { AccountId = Guid.NewGuid(), Date = new DateOnly(2026, 8, 1), PayeeRaw = "x" });
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Creates_the_required_indexes()
    {
        await OpenAsync();
        await using var db = _factory.CreateDbContext();
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();

        async Task<string[]> IndexColumns(string table)
        {
            var names = new List<string>();
            await using var list = connection.CreateCommand();
            list.CommandText = $"""SELECT name FROM pragma_index_list('{table}')""";
            await using (var reader = await list.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    names.Add(reader.GetString(0));
                }
            }

            var result = new List<string>();
            foreach (var name in names)
            {
                await using var info = connection.CreateCommand();
                info.CommandText = $"""SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_index_info('{name}') ORDER BY seqno)""";
                result.Add((string)(await info.ExecuteScalarAsync())!);
            }

            return [.. result];
        }

        var txnIndexes = await IndexColumns("Transactions");
        txnIndexes.ShouldContain("AccountId,Date");
        txnIndexes.ShouldContain("Date");
        txnIndexes.ShouldContain("CategoryId,Date");
        txnIndexes.ShouldContain("ImportFingerprint");
        txnIndexes.ShouldContain("ProviderTransactionId");
        txnIndexes.ShouldContain("PayeeId");
        (await IndexColumns("BudgetAssignments")).ShouldContain("Month");
        (await IndexColumns("BalanceSnapshots")).ShouldContain("AccountId,Date");
        (await IndexColumns("Payees")).ShouldContain("NormalizedName");
    }

    [Fact]
    public async Task Soft_deleted_transactions_and_their_splits_are_filtered()
    {
        await OpenAsync();
        var account = NewChecking();
        var groceries = new Category { GroupId = SystemIds.InflowGroup, Name = "Groceries" };
        var kept = new Transaction { AccountId = account.Id, Date = new DateOnly(2026, 8, 1), PayeeRaw = "Kept", Amount = -100 };
        var deleted = new Transaction { AccountId = account.Id, Date = new DateOnly(2026, 8, 2), PayeeRaw = "Deleted", Amount = -200, IsDeleted = true };
        deleted.Splits.Add(new TransactionSplit { CategoryId = groceries.Id, Amount = -200 });

        await using (var db = _factory.CreateDbContext())
        {
            db.AddRange(account, groceries, kept, deleted);
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            (await db.Transactions.Select(t => t.PayeeRaw).ToListAsync()).ShouldBe(["Kept"]);
            (await db.TransactionSplits.CountAsync()).ShouldBe(0);
            (await db.Transactions.IgnoreQueryFilters().CountAsync()).ShouldBe(2);
            (await db.TransactionSplits.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Accepts_splits_that_sum_to_the_parent_amount()
    {
        await OpenAsync();
        var (account, a, b) = await SeedSplitCategoriesAsync();
        var txn = new Transaction { AccountId = account, Date = new DateOnly(2026, 8, 10), PayeeRaw = "Costco", Amount = -100_00 };
        txn.Splits.Add(new TransactionSplit { CategoryId = a, Amount = -60_00 });
        txn.Splits.Add(new TransactionSplit { CategoryId = b, Amount = -40_00 });

        await using (var db = _factory.CreateDbContext())
        {
            db.Transactions.Add(txn);
            await db.SaveChangesAsync();
        }

        // Changing parent and splits together inside one transaction is allowed.
        await using (var db = _factory.CreateDbContext())
        {
            var loaded = await db.Transactions.Include(t => t.Splits).SingleAsync(t => t.Id == txn.Id);
            loaded.Amount = -110_00;
            loaded.Splits.First(s => s.CategoryId == a).Amount = -70_00;
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            (await db.TransactionSplits.SumAsync(s => s.Amount)).ShouldBe(-110_00);
        }
    }

    [Fact]
    public async Task Rejects_splits_that_do_not_sum_to_the_parent_amount()
    {
        await OpenAsync();
        var (account, a, b) = await SeedSplitCategoriesAsync();
        var txn = new Transaction { AccountId = account, Date = new DateOnly(2026, 8, 10), PayeeRaw = "Costco", Amount = -100_00 };
        txn.Splits.Add(new TransactionSplit { CategoryId = a, Amount = -60_00 });
        txn.Splits.Add(new TransactionSplit { CategoryId = b, Amount = -30_00 });

        await using (var db = _factory.CreateDbContext())
        {
            db.Transactions.Add(txn);
            var ex = await Should.ThrowAsync<Exception>(() => db.SaveChangesAsync());
            (ex.ToString()).ShouldContain("FOREIGN KEY constraint failed");
        }

        await using (var db = _factory.CreateDbContext())
        {
            (await db.Transactions.IgnoreQueryFilters().CountAsync()).ShouldBe(0, "the failed commit must roll back");
        }
    }

    [Fact]
    public async Task Rejects_changing_the_parent_amount_without_the_splits()
    {
        await OpenAsync();
        var (account, a, b) = await SeedSplitCategoriesAsync();
        var txn = new Transaction { AccountId = account, Date = new DateOnly(2026, 8, 10), PayeeRaw = "Costco", Amount = -100_00 };
        txn.Splits.Add(new TransactionSplit { CategoryId = a, Amount = -60_00 });
        txn.Splits.Add(new TransactionSplit { CategoryId = b, Amount = -40_00 });
        await using (var db = _factory.CreateDbContext())
        {
            db.Transactions.Add(txn);
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            var loaded = await db.Transactions.SingleAsync(t => t.Id == txn.Id);
            loaded.Amount = -90_00;
            await Should.ThrowAsync<Exception>(() => db.SaveChangesAsync());
        }

        await using (var db = _factory.CreateDbContext())
        {
            var loaded = await db.Transactions.SingleAsync(t => t.Id == txn.Id);
            loaded.CategoryId = a; // a split parent must not carry a category
            await Should.ThrowAsync<Exception>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Deleting_a_split_parent_cascades()
    {
        await OpenAsync();
        var (account, a, b) = await SeedSplitCategoriesAsync();
        var txn = new Transaction { AccountId = account, Date = new DateOnly(2026, 8, 10), PayeeRaw = "Costco", Amount = -100_00 };
        txn.Splits.Add(new TransactionSplit { CategoryId = a, Amount = -60_00 });
        txn.Splits.Add(new TransactionSplit { CategoryId = b, Amount = -40_00 });
        await using (var db = _factory.CreateDbContext())
        {
            db.Transactions.Add(txn);
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            (await db.Transactions.Where(t => t.Id == txn.Id).ExecuteDeleteAsync()).ShouldBe(1);
            (await db.TransactionSplits.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        }
    }

    [Fact]
    public async Task Refuses_files_from_a_newer_version()
    {
        var info = await OpenAsync();
        await using (var connection = new SqliteConnection(KeelDatabase.ConnectionString(info.Path)))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('29991231000000_FromTheFuture', '99.0.0')""";
            await command.ExecuteNonQueryAsync();
        }

        var service = new BudgetFileService(new KeelDbContextFactory(), NullLogger<BudgetFileService>.Instance);
        var ex = await Should.ThrowAsync<BudgetFileTooNewException>(() => service.OpenOrCreateAsync(info.Path, CancellationToken.None));
        ex.UnknownMigrations.ShouldBe(["29991231000000_FromTheFuture"]);
    }

    [Fact]
    public void Factory_requires_an_open_file()
    {
        Should.Throw<InvalidOperationException>(() => new KeelDbContextFactory().CreateDbContext());
    }

    private async Task<(Guid Account, Guid A, Guid B)> SeedSplitCategoriesAsync()
    {
        var account = NewChecking();
        var group = new CategoryGroup { Name = "Everyday", SortOrder = 2 };
        var a = new Category { GroupId = group.Id, Name = "Groceries" };
        var b = new Category { GroupId = group.Id, Name = "Household" };
        await using var db = _factory.CreateDbContext();
        db.AddRange(account, group, a, b);
        await db.SaveChangesAsync();
        return (account.Id, a.Id, b.Id);
    }

    private static async Task<string> ScalarAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }
}
