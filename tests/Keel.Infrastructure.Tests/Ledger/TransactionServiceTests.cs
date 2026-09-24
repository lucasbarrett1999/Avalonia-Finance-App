using Keel.Application.Ledger;
using Keel.Domain;
using Keel.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Ledger;

public sealed class TransactionServiceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    private ITransactionService Txns => _host.Transactions;

    [Fact]
    public async Task Adds_a_manual_transaction_and_creates_its_payee()
    {
        var checking = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");

        var txn = await _host.AddAsync(checking.Id, -4_250, "  Trader   Joe's ", groceries, memo: "weekly");

        txn.Amount.ShouldBe(-4_250);
        txn.Payee.ShouldBe("Trader Joe's");
        txn.CategoryId.ShouldBe(groceries);
        txn.Status.ShouldBe(TransactionStatus.Uncleared);
        txn.IsApproved.ShouldBeTrue();
        txn.Source.ShouldBe(TransactionSource.Manual);
        await using var db = _host.Db();
        (await db.Payees.SingleAsync(p => p.Id == txn.PayeeId)).NormalizedName.ShouldBe("TRADER JOE'S");

        var again = await _host.AddAsync(checking.Id, -100, "trader joe's");
        again.PayeeId.ShouldBe(txn.PayeeId);
    }

    [Fact]
    public async Task Edits_every_field()
    {
        var checking = await _host.CheckingAsync();
        var savings = await _host.AccountAsync("Cash box", AccountType.Cash);
        var dining = await _host.CategoryAsync("Dining");
        var txn = await _host.AddAsync(checking.Id, -1_000);

        var edited = await Txns.SaveAsync(
            new SaveTransactionRequest(txn.Id, savings.Id, new DateOnly(2026, 8, 12), -1_250, "Cafe", dining, "lunch", TransactionStatus.Cleared, false),
            Ct);

        edited.AccountId.ShouldBe(savings.Id);
        edited.Date.ShouldBe(new DateOnly(2026, 8, 12));
        edited.Amount.ShouldBe(-1_250);
        edited.Payee.ShouldBe("Cafe");
        edited.CategoryId.ShouldBe(dining);
        edited.Memo.ShouldBe("lunch");
        edited.Status.ShouldBe(TransactionStatus.Cleared);
        edited.IsApproved.ShouldBeFalse();
    }

    [Fact]
    public async Task Transfer_between_on_budget_accounts_is_one_logical_record_in_both_registers()
    {
        var checking = await _host.CheckingAsync();
        var savings = await _host.AccountAsync("Savings", AccountType.Savings);
        var groceries = await _host.CategoryAsync("Groceries");

        var txn = await Txns.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 5), -30_000, null, groceries, "top up", TransferAccountId: savings.Id), Ct);

        txn.TransferAccountId.ShouldBe(savings.Id);
        txn.CategoryId.ShouldBeNull();
        await using var db = _host.Db();
        var pair = await db.Transactions.SingleAsync(t => t.AccountId == savings.Id);
        pair.TransferPairId.ShouldBe(txn.TransferPairId);
        pair.TransferAccountId.ShouldBe(checking.Id);
        pair.Amount.ShouldBe(30_000);
        pair.Memo.ShouldBe("top up");
        pair.CategoryId.ShouldBeNull();

        (await _host.Accounts.GetAccountAsync(savings.Id, Ct))!.Balance.Amount.ShouldBe(30_000);
    }

    [Fact]
    public async Task Editing_either_side_updates_the_other()
    {
        var checking = await _host.CheckingAsync();
        var savings = await _host.AccountAsync("Savings", AccountType.Savings);
        var card = await _host.AccountAsync("Card", AccountType.CreditCard);
        var txn = await Txns.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 5), -30_000, null, null, null, TransferAccountId: savings.Id), Ct);

        Guid pairId;
        await using (var db = _host.Db())
        {
            pairId = (await db.Transactions.SingleAsync(t => t.AccountId == savings.Id)).Id;
        }

        // Edit from the savings side: amount, date, memo flow back to checking.
        await Txns.SaveAsync(new SaveTransactionRequest(pairId, savings.Id, new DateOnly(2026, 8, 6), 45_000, null, null, "moved", TransferAccountId: checking.Id), Ct);
        var original = (await Txns.GetAsync(txn.Id, Ct))!;
        original.Amount.ShouldBe(-45_000);
        original.Date.ShouldBe(new DateOnly(2026, 8, 6));
        original.Memo.ShouldBe("moved");

        // Retarget the transfer to the card: the counterpart moves accounts.
        await Txns.SaveAsync(new SaveTransactionRequest(txn.Id, checking.Id, new DateOnly(2026, 8, 6), -45_000, null, null, "card payment", TransferAccountId: card.Id), Ct);
        var counterpart = (await Txns.GetAsync(pairId, Ct))!;
        counterpart.AccountId.ShouldBe(card.Id);
        counterpart.TransferAccountId.ShouldBe(checking.Id);
        counterpart.Amount.ShouldBe(45_000);
        (await _host.Accounts.GetAccountAsync(savings.Id, Ct))!.Balance.Amount.ShouldBe(0);

        // Turning it back into a normal transaction removes the other side.
        await Txns.SaveAsync(new SaveTransactionRequest(txn.Id, checking.Id, new DateOnly(2026, 8, 6), -45_000, "Landlord", null, null), Ct);
        (await Txns.GetAsync(pairId, Ct))!.IsDeleted.ShouldBeTrue();
        (await Txns.GetAsync(txn.Id, Ct))!.TransferPairId.ShouldBeNull();
    }

    [Fact]
    public async Task Transfer_to_a_tracking_account_requires_a_category_on_the_on_budget_side()
    {
        var checking = await _host.CheckingAsync();
        var loan = await _host.AccountAsync("Mortgage", AccountType.Loan, -2_000_000);
        var housing = await _host.CategoryAsync("Mortgage", "Bills");
        var request = new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 1), -150_000, null, null, null, TransferAccountId: loan.Id);

        (await Should.ThrowAsync<LedgerValidationException>(() => Txns.SaveAsync(request, Ct))).Error.ShouldBe(LedgerError.CategoryRequiredForTransfer);

        var txn = await Txns.SaveAsync(request with { CategoryId = housing }, Ct);
        txn.CategoryId.ShouldBe(housing);
        await using var db = _host.Db();
        var loanSide = await db.Transactions.SingleAsync(t => t.AccountId == loan.Id && t.TransferPairId != null);
        loanSide.CategoryId.ShouldBeNull();
        loanSide.Amount.ShouldBe(150_000);

        // Entering it from the tracking side puts the category on the on-budget side too.
        var fromLoan = await Txns.SaveAsync(new SaveTransactionRequest(null, loan.Id, new DateOnly(2026, 9, 1), 150_000, null, housing, null, TransferAccountId: checking.Id), Ct);
        fromLoan.CategoryId.ShouldBeNull();
        var checkingSide = await db.Transactions.SingleAsync(t => t.TransferPairId == fromLoan.TransferPairId && t.AccountId == checking.Id);
        checkingSide.CategoryId.ShouldBe(housing);
        checkingSide.Amount.ShouldBe(-150_000);
    }

    [Fact]
    public async Task Transfer_validation()
    {
        var checking = await _host.CheckingAsync();
        (await Should.ThrowAsync<LedgerValidationException>(() => Txns.SaveAsync(
            new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 1), -1, null, null, null, TransferAccountId: checking.Id), Ct)))
            .Error.ShouldBe(LedgerError.TransferToSameAccount);
        (await Should.ThrowAsync<LedgerValidationException>(() => Txns.SaveAsync(
            new SaveTransactionRequest(null, Guid.NewGuid(), new DateOnly(2026, 8, 1), -1, null, null, null), Ct)))
            .Error.ShouldBe(LedgerError.AccountNotFound);
    }

    [Fact]
    public async Task Splits_must_balance_and_replace_the_category()
    {
        var checking = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        var household = await _host.CategoryAsync("Household");
        var request = new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 3), -10_000, "Target", groceries, null,
            Splits: [new SplitLine(groceries, "food", -6_000), new SplitLine(household, null, -3_000)]);

        (await Should.ThrowAsync<LedgerValidationException>(() => Txns.SaveAsync(request, Ct))).Error.ShouldBe(LedgerError.SplitSumMismatch);
        (await Should.ThrowAsync<LedgerValidationException>(() => Txns.SaveAsync(request with { Splits = [new SplitLine(groceries, null, -10_000)] }, Ct)))
            .Error.ShouldBe(LedgerError.SplitTooFewLines);

        var saved = await Txns.SaveAsync(request with { Splits = [new SplitLine(groceries, "food", -6_000), new SplitLine(household, null, -4_000)] }, Ct);
        saved.CategoryId.ShouldBeNull();
        saved.Splits.Count.ShouldBe(2);
        saved.Splits.Sum(s => s.Amount).ShouldBe(-10_000);

        // Editing the amount and splits together stays consistent; unsplitting restores a category.
        var resplit = await Txns.SaveAsync(request with { Id = saved.Id, Amount = -12_000, Splits = [new SplitLine(groceries, null, -2_000), new SplitLine(household, null, -10_000)] }, Ct);
        resplit.Splits.Select(s => s.Amount).ShouldBe([-2_000, -10_000], ignoreOrder: true);
        var unsplit = await Txns.SaveAsync(request with { Id = saved.Id, Splits = null }, Ct);
        unsplit.Splits.ShouldBeEmpty();
        unsplit.CategoryId.ShouldBe(groceries);
        await using var db = _host.Db();
        (await db.TransactionSplits.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Delete_and_restore_include_the_other_side_of_a_transfer()
    {
        var checking = await _host.CheckingAsync();
        var savings = await _host.AccountAsync("Savings", AccountType.Savings);
        var txn = await Txns.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 5), -5_000, null, null, null, TransferAccountId: savings.Id), Ct);

        (await Txns.DeleteAsync([txn.Id], Ct)).ShouldBe(1);
        await using (var db = _host.Db())
        {
            (await db.Transactions.CountAsync(t => t.TransferPairId == txn.TransferPairId)).ShouldBe(0);
            (await db.Transactions.IgnoreQueryFilters().CountAsync(t => t.TransferPairId == txn.TransferPairId && t.IsDeleted)).ShouldBe(2);
        }

        (await Txns.RestoreAsync([txn.Id], Ct)).ShouldBe(1);
        (await _host.Accounts.GetAccountAsync(savings.Id, Ct))!.Balance.Amount.ShouldBe(5_000);

        await Txns.DeleteAsync([txn.Id], Ct);
        (await Txns.PurgeDeletedAsync([txn.Id], Ct)).ShouldBe(1);
        await using (var db = _host.Db())
        {
            (await db.Transactions.IgnoreQueryFilters().CountAsync(t => t.TransferPairId == txn.TransferPairId)).ShouldBe(0);
        }
    }

    [Fact]
    public async Task Cleared_toggle_and_bulk_actions()
    {
        var checking = await _host.CheckingAsync(opening: 0);
        var savings = await _host.AccountAsync("Savings", AccountType.Savings);
        var groceries = await _host.CategoryAsync("Groceries");
        var a = await _host.AddAsync(checking.Id, -100);
        var b = await _host.AddAsync(checking.Id, -200);
        var split = await Txns.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 3), -300, "X", null, null,
            Splits: [new SplitLine(groceries, null, -100), new SplitLine(groceries, null, -200)]), Ct);
        var transfer = await Txns.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 3), -50, null, null, null, TransferAccountId: savings.Id), Ct);

        (await Txns.ToggleClearedAsync(a.Id, Ct)).ShouldBe(TransactionStatus.Cleared);
        (await Txns.ToggleClearedAsync(a.Id, Ct)).ShouldBe(TransactionStatus.Uncleared);
        (await Txns.SetClearedAsync([a.Id, b.Id], true, Ct)).ShouldBe(2);

        (await Txns.CategorizeAsync([a.Id, b.Id, split.Id, transfer.Id], groceries, Ct)).ShouldBe(2);
        (await Txns.GetAsync(split.Id, Ct))!.CategoryId.ShouldBeNull();
        (await Txns.GetAsync(transfer.Id, Ct))!.CategoryId.ShouldBeNull();

        await Txns.SaveAsync(new SaveTransactionRequest(b.Id, checking.Id, b.Date, b.Amount, "Grocer", groceries, null, TransactionStatus.Cleared, IsApproved: false), Ct);
        (await Txns.ApproveAsync([a.Id, b.Id], Ct)).ShouldBe(1);

        (await Txns.MoveToAccountAsync([a.Id, transfer.Id], savings.Id, Ct)).ShouldBe(1);
        (await Txns.GetAsync(a.Id, Ct))!.AccountId.ShouldBe(savings.Id);
        (await Txns.GetAsync(transfer.Id, Ct))!.AccountId.ShouldBe(checking.Id);
    }

    [Fact]
    public async Task Reconciliation_locks_cleared_rows_and_can_record_an_adjustment()
    {
        var checking = await _host.CheckingAsync(opening: 100_000); // starting balance is cleared
        var a = await _host.AddAsync(checking.Id, -2_500, date: new DateOnly(2026, 8, 10));
        var b = await _host.AddAsync(checking.Id, -7_000, date: new DateOnly(2026, 8, 20));
        var late = await _host.AddAsync(checking.Id, -1_000, date: new DateOnly(2026, 9, 2));
        await Txns.SetClearedAsync([a.Id, late.Id], true, Ct);

        var statementDate = new DateOnly(2026, 8, 31);
        var status = await Txns.GetReconciliationStatusAsync(checking.Id, statementDate, 97_000, Ct);
        status.ClearedBalance.ShouldBe(97_500);
        status.Difference.ShouldBe(-500);
        status.ClearedCount.ShouldBe(2);
        status.UnclearedCount.ShouldBe(1);

        (await Should.ThrowAsync<LedgerValidationException>(() => Txns.FinishReconciliationAsync(new FinishReconciliationRequest(checking.Id, statementDate, 97_000, false), Ct)))
            .Error.ShouldBe(LedgerError.ReconciliationNotBalanced);

        var result = await Txns.FinishReconciliationAsync(new FinishReconciliationRequest(checking.Id, statementDate, 97_000, true), Ct);
        result.Adjustment.ShouldBe(-500);
        result.LockedCount.ShouldBe(3);
        var adjustment = (await Txns.GetAsync(result.AdjustmentTransactionId!.Value, Ct))!;
        adjustment.Amount.ShouldBe(-500);
        adjustment.Status.ShouldBe(TransactionStatus.Reconciled);
        adjustment.CategoryId.ShouldBe(SystemIds.ReadyToAssignCategory);
        (await Txns.GetAsync(a.Id, Ct))!.Status.ShouldBe(TransactionStatus.Reconciled);
        (await Txns.GetAsync(b.Id, Ct))!.Status.ShouldBe(TransactionStatus.Uncleared);
        (await Txns.GetAsync(late.Id, Ct))!.Status.ShouldBe(TransactionStatus.Cleared);
        (await Txns.GetReconciliationStatusAsync(checking.Id, statementDate, 97_000, Ct)).Difference.ShouldBe(0);

        await using (var db = _host.Db())
        {
            var recon = await db.Reconciliations.SingleAsync();
            recon.StatementBalance.ShouldBe(97_000);
            recon.CompletedAt.ShouldNotBeNull();
        }

        // Reconciled rows are locked.
        (await Should.ThrowAsync<LedgerValidationException>(() => Txns.ToggleClearedAsync(a.Id, Ct))).Error.ShouldBe(LedgerError.ReconciledLocked);
        (await Should.ThrowAsync<LedgerValidationException>(() => Txns.DeleteAsync([a.Id], Ct))).Error.ShouldBe(LedgerError.ReconciledLocked);
        (await Should.ThrowAsync<LedgerValidationException>(() => Txns.SaveAsync(
            new SaveTransactionRequest(a.Id, checking.Id, a.Date, -9_999, "Grocer", null, null, TransactionStatus.Reconciled), Ct))).Error.ShouldBe(LedgerError.ReconciledLocked);
        var memoOnly = await Txns.SaveAsync(new SaveTransactionRequest(a.Id, checking.Id, a.Date, a.Amount, "Grocer", null, "note", TransactionStatus.Reconciled), Ct);
        memoOnly.Memo.ShouldBe("note");
        memoOnly.Status.ShouldBe(TransactionStatus.Reconciled);

        // A balanced statement finishes without an adjustment.
        var next = await Txns.FinishReconciliationAsync(new FinishReconciliationRequest(checking.Id, new DateOnly(2026, 9, 30), 96_000, false), Ct);
        next.AdjustmentTransactionId.ShouldBeNull();
        next.LockedCount.ShouldBe(1);
    }

    [Fact]
    public async Task Publishes_affected_accounts_and_months()
    {
        var checking = await _host.CheckingAsync();
        var savings = await _host.AccountAsync("Savings", AccountType.Savings);
        _host.Bus.Messages.Clear();

        var txn = await Txns.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 7, 31), -5, null, null, null, TransferAccountId: savings.Id), Ct);
        await Txns.SaveAsync(new SaveTransactionRequest(txn.Id, checking.Id, new DateOnly(2026, 8, 2), -5, null, null, null, TransferAccountId: savings.Id), Ct);

        var changes = _host.Bus.LedgerChanges;
        changes.Count.ShouldBe(2);
        changes[0].AccountIds.ShouldBe([checking.Id, savings.Id], ignoreOrder: true);
        changes[1].MonthsAffected.ShouldBe([new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 1)], ignoreOrder: true);
    }
}
