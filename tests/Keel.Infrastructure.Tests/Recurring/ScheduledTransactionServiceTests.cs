using Keel.Application.Import;
using Keel.Application.Ledger;
using Keel.Application.Recurring;
using Keel.Application.Scheduling;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Recurring;

/// <summary>Scheduled transactions on a real SQLite file (F-ACC-6, ADR 0035).</summary>
public sealed class ScheduledTransactionServiceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;
    private M5TestLedger _ledger = null!;

    private static CancellationToken Ct => CancellationToken.None;

    private static DateOnly Today => M5TestLedger.Today;

    public async Task InitializeAsync()
    {
        _host = await LedgerTestHost.CreateAsync();
        _ledger = new M5TestLedger(_host);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private IScheduledTransactionService Scheduled => _ledger.Scheduled;

    private async Task<ScheduledTransactionDto> CreateAsync(Guid account, string payee, long amount, string rule, DateOnly start, bool autoEnter, DateOnly? end = null, Guid? transfer = null, Guid? category = null)
    {
        var p = await _host.Payees.GetOrCreateAsync(payee, Ct);
        return await Scheduled.CreateAsync(new ScheduledTransactionEdit(account, p.Id, amount, category, transfer, "memo", rule, start, end, autoEnter), Ct);
    }

    [Fact]
    public async Task Rules_are_stored_explicit_with_count_as_the_end_date()
    {
        var checking = await _ledger.AccountAsync("Checking");
        var start = new DateOnly(2027, 1, 31);
        var rent = await CreateAsync(checking.Id, "Landlord", -1_200_00, "RRULE:FREQ=MONTHLY;COUNT=3", start, autoEnter: false);
        rent.Rule.ShouldBe("FREQ=MONTHLY;BYMONTHDAY=31");
        rent.NextDate.ShouldBe(start);
        rent.EndDate.ShouldBe(new DateOnly(2027, 3, 31));
        rent.Schedule.ShouldContain("until 2027-03-31");
        (await Scheduled.GetUpcomingAsync(start, start.AddYears(1), checking.Id, Ct)).Select(i => i.Date)
            .ShouldBe([start, new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 31)]);

        var check = Scheduled.CheckRule("FREQ=WEEKLY;INTERVAL=2", new DateOnly(2027, 1, 1), 5);
        check.IsValid.ShouldBeTrue();
        check.Description.ShouldBe("Every 2 weeks on Friday");
        check.NextDates.ShouldBe([new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 15), new DateOnly(2027, 1, 29), new DateOnly(2027, 2, 12), new DateOnly(2027, 2, 26)]);
        var bad = Scheduled.CheckRule("FREQ=MONTHLY;BYSETPOS=-1", start, 5);
        bad.IsValid.ShouldBeFalse();
        bad.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Auto_enter_creates_scheduled_transactions_advances_the_next_date_and_undoes_as_one_action()
    {
        var checking = await _ledger.AccountAsync("Checking");
        var gym = await CreateAsync(checking.Id, "Gym", -30_00, "FREQ=WEEKLY", Today.AddDays(-14), autoEnter: true);

        var result = await Scheduled.EnterDueAsync(Today, Ct);
        result.EnteredTransactionIds.Count.ShouldBe(3, "two weeks ago, last week and today");
        result.NeedsPrompt.ShouldBeEmpty();
        var entered = (await _host.Transactions.GetAsync(result.EnteredTransactionIds[0], Ct))!;
        entered.Source.ShouldBe(TransactionSource.Scheduled);
        entered.Amount.ShouldBe(-30_00);
        entered.Payee.ShouldBe("Gym");
        entered.Memo.ShouldBe("memo");
        await using (var db = _host.Db())
        {
            (await db.Transactions.CountAsync(t => t.ScheduledFromId == gym.Id)).ShouldBe(3);
        }

        (await Scheduled.GetByIdAsync(gym.Id, Ct))!.NextDate.ShouldBe(Today.AddDays(7));
        (await Scheduled.EnterDueAsync(Today, Ct)).EnteredTransactionIds.ShouldBeEmpty("nothing is due twice");

        _host.Undo.NextUndo.ShouldBe(LedgerAction.EnterScheduled);
        await _host.Undo.UndoAsync(Ct);
        (await Scheduled.GetByIdAsync(gym.Id, Ct))!.NextDate.ShouldBe(Today.AddDays(-14));
        foreach (var id in result.EnteredTransactionIds)
        {
            (await _host.Transactions.GetAsync(id, Ct)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task Prompted_instances_are_entered_or_skipped_one_at_a_time_in_order()
    {
        var checking = await _ledger.AccountAsync("Checking");
        var savings = await _ledger.AccountAsync("Savings", AccountType.Savings, 0);
        var move = await CreateAsync(checking.Id, "Savings plan", -100_00, $"FREQ=MONTHLY;BYMONTHDAY={Today.Day}", Today.AddMonths(-1), autoEnter: false, transfer: savings.Id);

        var due = await Scheduled.EnterDueAsync(Today, Ct);
        due.EnteredTransactionIds.ShouldBeEmpty();
        due.NeedsPrompt.Select(i => i.Date).ShouldBe([Today.AddMonths(-1), Today]);
        due.NeedsPrompt[0].IsNext.ShouldBeTrue();
        due.NeedsPrompt[1].IsNext.ShouldBeFalse();
        due.NeedsPrompt.ShouldAllBe(i => i.IsOverdue == (i.Date < Today) && i.TransferAccountName == "Savings");

        await Should.ThrowAsync<InvalidOperationException>(() => Scheduled.EnterInstanceAsync(move.Id, Today, Ct));
        await Scheduled.SkipInstanceAsync(move.Id, Today.AddMonths(-1), Ct);
        _host.Undo.NextUndo.ShouldBe(LedgerAction.SkipScheduled);
        var id = await Scheduled.EnterInstanceAsync(move.Id, Today, Ct);
        var txn = (await _host.Transactions.GetAsync(id, Ct))!;
        txn.TransferAccountId.ShouldBe(savings.Id);
        txn.Source.ShouldBe(TransactionSource.Scheduled);
        await using (var db = _host.Db())
        {
            var pair = await db.Transactions.SingleAsync(t => t.AccountId == savings.Id && t.TransferPairId == txn.TransferPairId);
            pair.Amount.ShouldBe(100_00);
            pair.ScheduledFromId.ShouldBe(move.Id);
        }

        (await Scheduled.GetByIdAsync(move.Id, Ct))!.NextDate.ShouldBe(Today.AddMonths(1));

        // Ghost rows: the savings register sees the other side of the scheduled transfer.
        var ghosts = await Scheduled.GetUpcomingAsync(Today, Today.AddMonths(2), savings.Id, Ct);
        ghosts.Select(g => (g.AccountId, g.Amount.Amount, g.IsNext)).ShouldBe([(savings.Id, 100_00, true), (savings.Id, 100_00, false)]);
    }

    [Fact]
    public async Task Every_two_weeks_keeps_its_fortnight_after_entries_and_edits_and_delete_is_undoable()
    {
        var checking = await _ledger.AccountAsync("Checking");
        var start = Today.AddDays(-21);
        var pay = await CreateAsync(checking.Id, "Employer", 2_000_00, "FREQ=WEEKLY;INTERVAL=2", start, autoEnter: true);
        var entered = await Scheduled.EnterDueAsync(Today, Ct);
        entered.EnteredTransactionIds.Count.ShouldBe(2);
        var next = (await Scheduled.GetByIdAsync(pay.Id, Ct))!.NextDate;
        next.ShouldBe(start.AddDays(28));
        (await Scheduled.GetUpcomingAsync(next, next.AddDays(30), null, Ct)).Select(i => i.Date).ShouldBe([next, next.AddDays(14), next.AddDays(28)]);

        var payee = await _host.Payees.GetOrCreateAsync("Employer", Ct);
        await Scheduled.UpdateAsync(pay.Id, new ScheduledTransactionEdit(checking.Id, payee.Id, 2_100_00, null, null, null, pay.Rule, next, null, true), Ct);
        (await Scheduled.GetByIdAsync(pay.Id, Ct))!.Amount.Amount.ShouldBe(2_100_00);

        await Scheduled.DeleteAsync(pay.Id, Ct);
        (await Scheduled.GetByIdAsync(pay.Id, Ct)).ShouldBeNull();
        await using (var db = _host.Db())
        {
            (await db.Transactions.CountAsync(t => t.ScheduledFromId != null)).ShouldBe(0, "entered rows stay, unlinked");
        }

        await _host.Undo.UndoAsync(Ct);
        (await Scheduled.GetByIdAsync(pay.Id, Ct)).ShouldNotBeNull();
        await using (var db = _host.Db())
        {
            (await db.Transactions.CountAsync(t => t.ScheduledFromId == pay.Id)).ShouldBe(2, "undo restores the links");
        }
    }

    [Fact]
    public async Task A_schedule_created_from_a_recurring_item_is_linked_to_it()
    {
        var checking = await _ledger.AccountAsync("Checking");
        await _ledger.MonthlyAsync(checking.Id, "Phone Co", -45_00, 4, Today.AddDays(-3));
        await _ledger.Recurring.DetectAsync(Today, Ct);
        var item = await _ledger.ItemAsync("Phone Co");
        var schedule = await Scheduled.CreateAsync(
            new ScheduledTransactionEdit(checking.Id, item.PayeeId, -45_00, null, null, null, "FREQ=MONTHLY", item.NextExpectedDate, null, false, item.Id), Ct);
        (await _ledger.ItemAsync("Phone Co")).ScheduledTransactionId.ShouldBe(schedule.Id);
        _host.Bus.Messages.OfType<RecurringChanged>().Last().ItemIds.ShouldBe([item.Id]);
    }

    [Fact]
    public async Task Imported_transactions_run_through_detection_once()
    {
        var checking = await _ledger.AccountAsync("Checking");
        var recurring = _ledger.Recurring;
        (await recurring.DetectNewImportsAsync(Today, Ct)).ShouldBeNull("the first call only sets the starting point");
        var rows = Enumerable.Range(0, 4).Select(i => new IncomingTransaction(Today.AddDays(-2).AddMonths(-i), -12_00, "HULU *SUBSCRIPTION")).ToList();
        (await _host.Get<IImportService>().ImportTransactionsAsync(TransactionSource.File, new ImportBatch(checking.Id, rows), Ct)).Added.ShouldBe(4);

        var summary = await recurring.DetectNewImportsAsync(Today, Ct);
        summary.ShouldNotBeNull();
        summary.Created.ShouldBe(1);
        (await recurring.DetectNewImportsAsync(Today, Ct)).ShouldBeNull("already processed");
        await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, checking.Id, Today, -5_00, "Manual", null, null), Ct);
        (await recurring.DetectNewImportsAsync(Today, Ct)).ShouldBeNull("manual entry is not an import");
    }
}
