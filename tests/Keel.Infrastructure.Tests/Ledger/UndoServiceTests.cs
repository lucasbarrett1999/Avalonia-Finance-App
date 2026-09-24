using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Ledger;

public sealed class UndoServiceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    private IUndoService Undo => _host.Undo;

    [Fact]
    public async Task Undo_and_redo_of_an_added_transaction()
    {
        var checking = await _host.CheckingAsync();
        var txn = await _host.AddAsync(checking.Id, -1_234, "New Payee");
        Undo.NextUndo.ShouldBe(LedgerAction.AddTransaction);

        (await Undo.UndoAsync(Ct)).ShouldBe(LedgerAction.AddTransaction);
        (await _host.Transactions.GetAsync(txn.Id, Ct)).ShouldBeNull();
        await using (var db = _host.Db())
        {
            (await db.Payees.AnyAsync(p => p.Name == "New Payee")).ShouldBeFalse("the payee was created by the undone action");
        }

        Undo.CanRedo.ShouldBeTrue();
        (await Undo.RedoAsync(Ct)).ShouldBe(LedgerAction.AddTransaction);
        var back = (await _host.Transactions.GetAsync(txn.Id, Ct))!;
        back.Amount.ShouldBe(-1_234);
        back.Payee.ShouldBe("New Payee");
        Undo.CanRedo.ShouldBeFalse();
    }

    [Fact]
    public async Task Undo_restores_a_deleted_transfer_and_an_edited_split_exactly()
    {
        var checking = await _host.CheckingAsync();
        var savings = await _host.AccountAsync("Savings", AccountType.Savings);
        var a = await _host.CategoryAsync("A");
        var b = await _host.CategoryAsync("B");
        var transfer = await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 2), -700, null, null, null, TransferAccountId: savings.Id), Ct);
        var split = await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 3), -1_000, "Shop", null, "orig",
            Splits: [new SplitLine(a, "x", -400), new SplitLine(b, "y", -600)]), Ct);

        await _host.Transactions.DeleteAsync([transfer.Id], Ct);
        await _host.Transactions.SaveAsync(new SaveTransactionRequest(split.Id, checking.Id, split.Date, -1_500, "Shop", a, "changed"), Ct);

        await Undo.UndoAsync(Ct);
        var restoredSplit = (await _host.Transactions.GetAsync(split.Id, Ct))!;
        restoredSplit.Amount.ShouldBe(-1_000);
        restoredSplit.Memo.ShouldBe("orig");
        restoredSplit.CategoryId.ShouldBeNull();
        restoredSplit.Splits.OrderBy(s => s.Amount).Select(s => (s.CategoryId, s.Memo, s.Amount)).ShouldBe([(b, "y", -600L), (a, "x", -400L)]);

        await Undo.UndoAsync(Ct);
        (await _host.Accounts.GetAccountAsync(savings.Id, Ct))!.Balance.Amount.ShouldBe(700);
        (await _host.Transactions.GetAsync(transfer.Id, Ct))!.IsDeleted.ShouldBeFalse();

        await Undo.RedoAsync(Ct);
        (await _host.Transactions.GetAsync(transfer.Id, Ct))!.IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Undo_of_account_creation_removes_everything_it_created()
    {
        var card = await _host.AccountAsync("Visa", AccountType.CreditCard, -5_000);
        await Undo.UndoAsync(Ct);

        (await _host.Accounts.GetAccountAsync(card.Id, Ct)).ShouldBeNull();
        await using var db = _host.Db();
        (await db.Categories.AnyAsync(c => c.LinkedAccountId == card.Id)).ShouldBeFalse();
        (await db.Transactions.IgnoreQueryFilters().AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Undo_and_redo_are_audited_and_new_actions_clear_redo()
    {
        var checking = await _host.CheckingAsync(opening: 0);
        var txn = await _host.AddAsync(checking.Id, -50);
        await _host.Transactions.ToggleClearedAsync(txn.Id, Ct);
        await Undo.UndoAsync(Ct);

        (await _host.Transactions.GetAsync(txn.Id, Ct))!.Status.ShouldBe(TransactionStatus.Uncleared);
        await using (var db = _host.Db())
        {
            var last = await db.AuditEvents.Where(e => e.EntityType == "Transaction").OrderByDescending(e => e.Id).FirstAsync();
            last.Kind.ShouldBe(AuditEventKind.Updated);
            last.BeforeJson!.ShouldContain("\"Status\":\"Cleared\"");
            last.AfterJson!.ShouldContain("\"Status\":\"Uncleared\"");
        }

        Undo.CanRedo.ShouldBeTrue();
        await _host.AddAsync(checking.Id, -60);
        Undo.CanRedo.ShouldBeFalse();
    }

    [Fact]
    public async Task Keeps_the_last_fifty_actions()
    {
        var checking = await _host.CheckingAsync(opening: 0);
        var txn = await _host.AddAsync(checking.Id, -50);
        var changed = 0;
        Undo.Changed += (_, _) => Interlocked.Increment(ref changed);
        for (var i = 0; i < IUndoService.Capacity + 5; i++)
        {
            await _host.Transactions.ToggleClearedAsync(txn.Id, Ct);
        }

        changed.ShouldBe(IUndoService.Capacity + 5);
        var undone = 0;
        while (await Undo.UndoAsync(Ct) is not null)
        {
            undone++;
        }

        undone.ShouldBe(IUndoService.Capacity);
        (await _host.Transactions.GetAsync(txn.Id, Ct)).ShouldNotBeNull("the add fell off the stack");
    }

    [Fact]
    public async Task Undo_publishes_ledger_changes()
    {
        var checking = await _host.CheckingAsync(opening: 0);
        await _host.AddAsync(checking.Id, -50, date: new DateOnly(2026, 6, 15));
        _host.Bus.Messages.Clear();

        await Undo.UndoAsync(Ct);

        var change = _host.Bus.LedgerChanges.ShouldHaveSingleItem();
        change.AccountIds.ShouldBe([checking.Id]);
        change.MonthsAffected.ShouldBe([new DateOnly(2026, 6, 1)]);
    }
}
