using System.Text;
using Keel.Application.Accounts;
using Keel.Application.Messaging;
using Keel.Application.Sync;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Infrastructure.Sync;
using Keel.Infrastructure.Sync.Plaid;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Infrastructure.Tests.Sync;

/// <summary>F-TXN-3 acceptance over the real pipeline and SQLite, with Plaid faked at the HTTP layer.</summary>
public sealed class SyncServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateOnly Day = new(2026, 9, 10);

    private static void History(FakeItem item)
    {
        item.Add("t1", "acc-checking", Day, 12.34m, "SQ *BLUE BOTTLE 1234", "Blue Bottle Coffee");
        item.Add("t2", "acc-checking", Day.AddDays(1), -1500.00m, "ACME PAYROLL", null);
        item.Add("t3", "acc-credit", Day.AddDays(2), 55.10m, "TRADER JOE'S #552", "Trader Joe's");
    }

    [Fact]
    public async Task Link_creates_the_connection_and_accounts_and_keeps_the_token_out_of_the_database()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var (connection, item) = await host.LinkAsync();

        connection.InstitutionName.ShouldBe("First Platypus Bank");
        connection.Provider.ShouldBe(SyncProvider.Plaid);
        connection.Status.ShouldBe(SyncStatus.Ok);
        connection.Accounts.Select(a => a.ProviderAccountId).ShouldBe(["acc-checking", "acc-credit"], ignoreOrder: true);

        var stored = await host.ConnectionAsync(connection.Id);
        stored.ExternalItemId.ShouldBe(item.ItemId);
        stored.SecretRef.ShouldBe(SecretKeys.Connection(connection.Id));
        (await host.Secrets.GetAsync(stored.SecretRef))!.ShouldContain(item.AccessToken);

        var card = await host.AccountAsync(await host.AccountIdAsync("acc-credit"));
        card!.Type.ShouldBe(AccountType.CreditCard);
        card.SyncStatus.ShouldBe(SyncStatus.Ok);

        // PRD 6.7: secrets never in the database file.
        SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(host.FilePath);
        var text = Encoding.UTF8.GetString(bytes) + Encoding.Unicode.GetString(bytes);
        text.ShouldNotContain(item.AccessToken);
        text.ShouldNotContain(FakePlaidServer.Secret);
    }

    [Fact]
    public async Task Initial_sync_imports_through_the_pipeline_and_records_balances_as_snapshots()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var (connection, _) = await host.LinkAsync(History);

        var result = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);

        result.Succeeded.ShouldBeTrue();
        (result.Added, result.Updated, result.Removed).ShouldBe((3, 0, 0));
        var checking = await host.AccountIdAsync("acc-checking");
        var rows = await host.RowsAsync(checking);
        rows.Select(r => (r.ProviderTransactionId, r.Amount)).ShouldBe([("t1", -1234L), ("t2", 150_000L)]);
        rows.ShouldAllBe(r => r.Source == TransactionSource.Provider && !r.IsApproved && r.Status == TransactionStatus.Cleared);
        rows[0].PayeeRaw.ShouldBe("Blue Bottle Coffee");

        var stored = await host.ConnectionAsync(connection.Id);
        stored.Cursor.ShouldBe("c3");
        stored.LastSyncAt.ShouldNotBeNull();
        stored.Status.ShouldBe(SyncStatus.Ok);

        await using var db = host.Db();
        var snapshots = await db.BalanceSnapshots.AsNoTracking().ToListAsync();
        snapshots.Select(s => s.Balance).ShouldBe([11_000L, -41_000L], ignoreOrder: true);
        snapshots.ShouldAllBe(s => s.Source == BalanceSource.Provider);
        (await host.AccountAsync(checking))!.ReportedBalance!.Value.Amount.ShouldBe(11_000);
        host.Bus.LedgerChanges.ShouldNotBeEmpty();
        host.Bus.Messages.OfType<SyncConnectionsChanged>().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_new_linked_account_starts_at_the_bank_balance_once_history_is_in()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var (connection, item) = await host.LinkAsync(i =>
        {
            History(i);
            i.HistoryComplete = false;
        });

        await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        var checking = await host.AccountIdAsync("acc-checking");
        (await host.AccountAsync(checking))!.ClearedBalance.Amount.ShouldBe(150_000 - 1234);

        item.HistoryComplete = true;
        await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);

        var account = (await host.AccountAsync(checking))!;
        account.ClearedBalance.Amount.ShouldBe(11_000);
        account.ReportedBalance!.Value.Amount.ShouldBe(11_000);
        account.OpeningDate.ShouldBe(Day);
        var card = (await host.AccountAsync(await host.AccountIdAsync("acc-credit")))!;
        card.ClearedBalance.Amount.ShouldBe(-41_000);
    }

    [Fact]
    public async Task Linking_an_existing_account_keeps_its_history_and_adds_no_starting_balance()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var existing = await host.Get<IAccountService>().CreateAccountAsync(new CreateAccountRequest("Plaid Checking", AccountType.Checking, "USD", new DateOnly(2026, 1, 1), 50_000), Ct);

        var session = await host.Sync.BeginLinkAsync("plaid", Ct);
        var completing = host.Sync.CompleteLinkAsync(session, Ct);
        History(host.Plaid.CompleteLink(session.SessionToken)!);
        var pending = await completing;

        var checkingRow = pending.Accounts.Single(a => a.Account.ProviderAccountId == "acc-checking");
        checkingRow.SuggestedExistingAccountId.ShouldBe(existing.Id);
        checkingRow.Balance!.Current.ShouldBe(11_000);
        pending.Accounts.Single(a => a.Account.ProviderAccountId == "acc-credit").Balance!.Current.ShouldBe(-41_000);

        var connection = await host.Sync.SaveLinkAsync(
            pending,
            [
                new AccountLinkChoice("acc-checking", AccountLinkAction.LinkExisting, existing.Id),
                new AccountLinkChoice("acc-credit", AccountLinkAction.Skip),
            ],
            Ct);
        connection.Accounts.ShouldHaveSingleItem().AccountId.ShouldBe(existing.Id);

        var result = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        result.Added.ShouldBe(2);
        var account = (await host.AccountAsync(existing.Id))!;
        account.ClearedBalance.Amount.ShouldBe(50_000 - 1234 + 150_000);
        account.ReportedBalance!.Value.Amount.ShouldBe(11_000);

        await using var db = host.Db();
        (await db.Accounts.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Incremental_sync_advances_the_cursor_and_adds_no_duplicates()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var (connection, item) = await host.LinkAsync(History);
        await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        var checking = await host.AccountIdAsync("acc-checking");

        var again = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        (again.Added, again.Updated, again.Removed).ShouldBe((0, 0, 0));
        (await host.ConnectionAsync(connection.Id)).Cursor.ShouldBe("c3");

        item.Add("t4", "acc-checking", Day.AddDays(5), 8.00m, "CHIPOTLE 0420", "Chipotle");
        item.Add("t5", "acc-checking", Day.AddDays(6), 20.00m, "SHELL OIL 5738", "Shell");
        var incremental = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        incremental.Added.ShouldBe(2);
        (await host.ConnectionAsync(connection.Id)).Cursor.ShouldBe("c5");
        (await host.RowsAsync(checking)).Count.ShouldBe(4);

        // A lost cursor replays the whole log: provider ids dedup every row.
        await using (var db = host.Db())
        {
            await db.SyncConnections.ExecuteUpdateAsync(s => s.SetProperty(c => c.Cursor, (string?)null));
        }

        var replay = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        replay.Added.ShouldBe(0);
        (await host.RowsAsync(checking)).Count.ShouldBe(4);
    }

    [Fact]
    public async Task Pages_are_imported_one_by_one_and_the_cursor_only_moves_after_a_page_commits()
    {
        await using var host = await SyncTestHost.CreateAsync(configure: s => s.AddSingleton(new PlaidProviderOptions { PollInterval = TimeSpan.FromMilliseconds(5), PageSize = 2 }));
        var (connection, item) = await host.LinkAsync(i =>
        {
            for (var n = 1; n <= 5; n++)
            {
                i.Add("p" + n, "acc-checking", Day.AddDays(n), n, "STORE " + n, "Store " + n);
            }
        });

        // The bank asks for a new login while the third page is fetched.
        host.Plaid.LoginRequiredFromPosition = 4;
        var failed = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        failed.Status.ShouldBe(SyncStatus.NeedsReauth);
        failed.Added.ShouldBe(4);
        var stored = await host.ConnectionAsync(connection.Id);
        stored.Cursor.ShouldBe("c4");
        stored.Status.ShouldBe(SyncStatus.NeedsReauth);
        stored.LastError.ShouldBe("ITEM_LOGIN_REQUIRED");

        host.Plaid.LoginRequiredFromPosition = null;
        var resumed = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        resumed.Added.ShouldBe(1);
        (await host.RowsAsync(await host.AccountIdAsync("acc-checking"))).Count.ShouldBe(5);
        host.Plaid.Requests.Count(r => r == "/transactions/sync").ShouldBe(4);
    }

    [Fact]
    public async Task A_mutation_during_pagination_restarts_from_the_first_cursor_without_duplicates()
    {
        await using var host = await SyncTestHost.CreateAsync(configure: s => s.AddSingleton(new PlaidProviderOptions { PollInterval = TimeSpan.FromMilliseconds(5), PageSize = 2 }));
        var (connection, item) = await host.LinkAsync(i =>
        {
            for (var n = 1; n <= 5; n++)
            {
                i.Add("m" + n, "acc-checking", Day.AddDays(n), n, "SHOP " + n, "Shop " + n);
            }
        });
        host.Plaid.FailNextPageWithMutation = true;

        var result = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);

        result.Succeeded.ShouldBeTrue();
        result.Added.ShouldBe(5);
        (await host.RowsAsync(await host.AccountIdAsync("acc-checking"))).Count.ShouldBe(5);
    }

    [Fact]
    public async Task Pending_rows_become_posted_in_place_through_the_pipeline()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var (connection, item) = await host.LinkAsync(i => i.Add("pend-1", "acc-checking", Day, 42.00m, "AMAZON MKTP US", "Amazon", pending: true));
        await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        var checking = await host.AccountIdAsync("acc-checking");
        var pending = (await host.RowsAsync(checking)).ShouldHaveSingleItem();
        pending.Status.ShouldBe(TransactionStatus.Uncleared);
        pending.ProviderTransactionId.ShouldBe("pend-1");

        // The user categorizes the pending row; posting must keep it.
        var groceries = await host.Get<Keel.Application.Categories.ICategoryService>().CreateCategoryAsync("Everyday", "Shopping", Ct);
        await host.Get<Keel.Application.Ledger.ITransactionService>().CategorizeAsync([pending.Id], groceries.Id, Ct);

        item.Add("post-1", "acc-checking", Day.AddDays(1), 41.50m, "AMAZON MKTP US", "Amazon", pendingId: "pend-1");
        item.Remove("pend-1", "acc-checking");
        var result = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);

        (result.Added, result.Updated, result.Removed).ShouldBe((0, 1, 0));
        var posted = (await host.RowsAsync(checking)).ShouldHaveSingleItem();
        posted.Id.ShouldBe(pending.Id);
        posted.ProviderTransactionId.ShouldBe("post-1");
        posted.Status.ShouldBe(TransactionStatus.Cleared);
        posted.Amount.ShouldBe(-4150);
        posted.Date.ShouldBe(Day.AddDays(1));
        posted.CategoryId.ShouldBe(groceries.Id);
    }

    [Fact]
    public async Task Modified_rows_update_in_place_and_removed_rows_are_soft_deleted()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var (connection, item) = await host.LinkAsync(History);
        await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        var checking = await host.AccountIdAsync("acc-checking");

        item.Modify(new FakeTransaction("t1", "acc-checking", Day, 13.00m, "SQ *BLUE BOTTLE 1234", "Blue Bottle Coffee", false, null));
        item.Remove("t2", "acc-checking");
        var result = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);

        (result.Added, result.Updated, result.Removed).ShouldBe((0, 1, 1));
        var rows = await host.RowsAsync(checking, includeDeleted: true);
        rows.Single(r => r.ProviderTransactionId == "t1").Amount.ShouldBe(-1300);
        rows.Single(r => r.ProviderTransactionId == "t2").IsDeleted.ShouldBeTrue();
        (await host.RowsAsync(checking)).ShouldHaveSingleItem();

        // Removal is an ordinary undoable delete.
        (await host.Get<IUndoService>().UndoAsync(Ct)).ShouldBe(LedgerAction.DeleteTransactions);
        (await host.RowsAsync(checking)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Item_login_required_is_a_reconnect_state_and_update_mode_repairs_it()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var (broken, item) = await host.LinkAsync(History);
        var (healthy, other) = await host.LinkAsync(i => i.Add("x1", "acc-checking", Day, 1m, "OTHER", "Other"));
        item.LoginRequired = true;

        var all = await host.Sync.SyncAllAsync(null, Ct);

        all.Failed.ShouldBe(1);
        all.Connections.Single(c => c.ConnectionId == healthy.Id).Succeeded.ShouldBeTrue();
        var failed = all.Connections.Single(c => c.ConnectionId == broken.Id);
        (failed.Status, failed.ErrorCode).ShouldBe((SyncStatus.NeedsReauth, "ITEM_LOGIN_REQUIRED"));
        var accountId = broken.Accounts[0].AccountId;
        (await host.AccountAsync(accountId))!.SyncStatus.ShouldBe(SyncStatus.NeedsReauth);
        (await host.Sync.GetConnectionsAsync(Ct)).Single(c => c.Id == broken.Id).Status.ShouldBe(SyncStatus.NeedsReauth);

        var session = await host.Sync.BeginReconnectAsync(broken.Id, Ct);
        var body = host.Plaid.Bodies.Last();
        ((string?)body["access_token"]).ShouldBe(item.AccessToken);
        body["products"].ShouldBeNull();
        session.ExistingConnectionId.ShouldBe(broken.Id.ToString());

        var completing = host.Sync.CompleteReconnectAsync(session, Ct);
        host.Plaid.CompleteLink(session.SessionToken);
        (await completing).Status.ShouldBe(SyncStatus.Ok);

        (await host.Sync.SyncConnectionAsync(broken.Id, null, Ct)).Added.ShouldBe(3);
        (await host.AccountAsync(accountId))!.SyncStatus.ShouldBe(SyncStatus.Ok);
    }

    [Fact]
    public async Task Unlink_removes_the_item_and_token_and_keeps_local_transactions()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var (connection, item) = await host.LinkAsync(History);
        await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        var checking = await host.AccountIdAsync("acc-checking");
        var before = await host.RowsAsync(checking);

        await host.Sync.UnlinkAsync(connection.Id, Ct);

        item.Removed.ShouldBeTrue();
        host.Secrets.Contains(SecretKeys.Connection(connection.Id)).ShouldBeFalse();
        (await host.Sync.GetConnectionsAsync(Ct)).ShouldBeEmpty();
        var account = (await host.AccountAsync(checking))!;
        account.SyncStatus.ShouldBeNull();
        (await host.RowsAsync(checking)).Select(r => r.Id).ShouldBe(before.Select(r => r.Id));
        (await host.Sync.SyncAccountAsync(checking, null, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_cancelled_or_exited_link_writes_nothing_and_a_discarded_link_is_removed_at_Plaid()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var session = await host.Sync.BeginLinkAsync("plaid", Ct);
        var completing = host.Sync.CompleteLinkAsync(session, Ct);
        host.Plaid.ExitLink(session.SessionToken);
        (await Should.ThrowAsync<BankProviderException>(completing)).Code.ShouldBe(BankErrorCodes.LinkExited);

        var second = await host.Sync.BeginLinkAsync("plaid", Ct);
        var pendingTask = host.Sync.CompleteLinkAsync(second, Ct);
        var item = host.Plaid.CompleteLink(second.SessionToken)!;
        var pending = await pendingTask;
        host.Secrets.Contains(SecretKeys.Connection(pending.ConnectionId)).ShouldBeTrue();

        await host.Sync.DiscardLinkAsync(pending, Ct);

        item.Removed.ShouldBeTrue();
        host.Secrets.Contains(SecretKeys.Connection(pending.ConnectionId)).ShouldBeFalse();
        (await host.Sync.GetConnectionsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_link_the_user_never_finishes_times_out()
    {
        await using var host = await SyncTestHost.CreateAsync(configure: s => s.AddSingleton(new PlaidProviderOptions { PollInterval = TimeSpan.FromMilliseconds(5), LinkTimeout = TimeSpan.FromMilliseconds(200) }));
        var session = await host.Sync.BeginLinkAsync("plaid", Ct);
        (await Should.ThrowAsync<BankProviderException>(host.Sync.CompleteLinkAsync(session, Ct))).Code.ShouldBe(BankErrorCodes.LinkExpired);
        host.Plaid.Requests.Count(r => r == "/link/token/get").ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task Linking_without_keys_fails_with_a_missing_credentials_code()
    {
        await using var host = await SyncTestHost.CreateAsync(withKeys: false);
        (await Should.ThrowAsync<BankProviderException>(host.Sync.BeginLinkAsync("plaid", Ct))).Code.ShouldBe(BankErrorCodes.MissingCredentials);
        host.Plaid.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Transient_server_errors_are_retried()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var (connection, _) = await host.LinkAsync(History);
        host.Plaid.TransientFailures = 2;

        var result = await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);

        result.Succeeded.ShouldBeTrue();
        result.Added.ShouldBe(3);
    }

    [Fact]
    public async Task Logs_never_contain_secrets_tokens_or_payees()
    {
        await using var host = await SyncTestHost.CreateAsync();
        var (connection, item) = await host.LinkAsync(History);
        await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        item.LoginRequired = true;
        await host.Sync.SyncConnectionAsync(connection.Id, null, Ct);
        await host.Sync.UnlinkAsync(connection.Id, Ct);

        var logs = host.Logs.All;
        logs.ShouldNotBeEmpty();
        logs.ShouldContain("ITEM_LOGIN_REQUIRED");
        foreach (var secret in new[] { FakePlaidServer.Secret, FakePlaidServer.ClientId, item.AccessToken, item.PublicToken, "link-sandbox-", "Blue Bottle", "TRADER JOE" })
        {
            logs.ShouldNotContain(secret);
        }
    }

    [Fact]
    public async Task Sync_settings_round_trip_through_the_budget_file()
    {
        await using var host = await SyncTestHost.CreateAsync();
        (await host.Sync.GetSettingsAsync(Ct)).ShouldBe(new SyncSettings());
        await host.Sync.SaveSettingsAsync(new SyncSettings(12, SyncOnStart: false), Ct);
        (await host.Sync.GetSettingsAsync(Ct)).ShouldBe(new SyncSettings(12, false));
    }
}
