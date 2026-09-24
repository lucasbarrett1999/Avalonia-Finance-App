using Keel.Application.Budget;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Budgeting;

/// <summary>Budget actions on the session undo stack, and recomputing over loaded ledger data (ADR 0040).</summary>
public sealed class BudgetUndoAndLedgerDataTests : IAsyncLifetime
{
    private static readonly DateOnly Aug = new(2026, 8, 1);
    private static readonly DateOnly Sep = new(2026, 9, 1);
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    private IBudgetService Budget => _host.Get<IBudgetService>();

    private IUndoService Undo => _host.Undo;

    private static BudgetCategoryDto Row(BudgetMonthDto month, Guid id) => month.Groups.SelectMany(g => g.Categories).Single(c => c.Id == id);

    // PRD 6.4.7 through the real services: Checking +3,000 RTA, Visa, Groceries 400 / Rent 1,500.
    private async Task<(Guid Checking, Guid Visa, Guid Groceries, Guid Rent)> Example647Async()
    {
        var checking = await _host.CheckingAsync(opening: 0);
        var visa = await _host.AccountAsync("Visa", AccountType.CreditCard);
        var groceries = await _host.CategoryAsync("Groceries");
        var rent = await _host.CategoryAsync("Rent", "Bills");
        await _host.AddAsync(checking.Id, 3_000_00, "Employer", SystemIdsRta, new DateOnly(2026, 8, 1));
        await _host.AddAsync(checking.Id, -1_500_00, "Landlord", rent, new DateOnly(2026, 8, 5));
        await _host.AddAsync(visa.Id, -250_00, "Grocer", groceries, new DateOnly(2026, 8, 10));
        await _host.AddAsync(visa.Id, -200_00, "Grocer", groceries, new DateOnly(2026, 8, 20));
        await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 25), -100_00, null, null, null, TransferAccountId: visa.Id), Ct);
        await Budget.AssignAsync(rent, Aug, 1_500_00, Ct);
        await Budget.AssignAsync(groceries, Aug, 400_00, Ct);
        return (checking.Id, visa.Id, groceries, rent);
    }

    private static Guid SystemIdsRta => Keel.Domain.Entities.SystemIds.ReadyToAssignCategory;

    [Fact]
    public async Task Assignments_join_the_undo_stack_and_undo_publishes_budget_changed_only()
    {
        var (_, _, groceries, _) = await Example647Async();
        Undo.NextUndo.ShouldBe(LedgerAction.AssignBudget);
        (await Budget.GetMonthAsync(Aug, Ct)).ReadyToAssign.Amount.ShouldBe(1_100_00);

        await Budget.AssignAsync(groceries, Aug, 450_00, Ct);
        (await Budget.GetMonthAsync(Aug, Ct)).ReadyToAssign.Amount.ShouldBe(1_050_00);
        _host.Bus.Messages.Clear();

        (await Undo.UndoAsync(Ct)).ShouldBe(LedgerAction.AssignBudget);
        var aug = await Budget.GetMonthAsync(Aug, Ct);
        aug.ReadyToAssign.Amount.ShouldBe(1_100_00);
        Row(aug, groceries).Assigned.Amount.ShouldBe(400_00);
        _host.Bus.Messages.ShouldHaveSingleItem().ShouldBeOfType<BudgetChanged>().Months.ShouldBe([Aug]);

        (await Undo.RedoAsync(Ct)).ShouldBe(LedgerAction.AssignBudget);
        Row(await Budget.GetMonthAsync(Aug, Ct), groceries).Assigned.Amount.ShouldBe(450_00);

        // Undoing the first assignment of a category deletes its row again (absent row means 0).
        await Undo.UndoAsync(Ct);
        await Undo.UndoAsync(Ct);
        Row(await Budget.GetMonthAsync(Aug, Ct), groceries).Assigned.Amount.ShouldBe(0);
        await using var db = _host.Db();
        (await db.BudgetAssignments.AnyAsync(a => a.CategoryId == groceries)).ShouldBeFalse();
    }

    [Fact]
    public async Task Move_money_fund_targets_and_targets_are_undoable()
    {
        var (_, _, groceries, rent) = await Example647Async();
        await Budget.MoveMoneyAsync(new MoveMoneyRequest(Aug, rent, groceries, 50_00), Ct);
        Undo.NextUndo.ShouldBe(LedgerAction.MoveMoney);
        await Undo.UndoAsync(Ct);
        var aug = await Budget.GetMonthAsync(Aug, Ct);
        Row(aug, rent).Assigned.Amount.ShouldBe(1_500_00);
        Row(aug, groceries).Assigned.Amount.ShouldBe(400_00);

        await Budget.SetTargetAsync(new TargetDto(groceries, TargetType.MonthlySetAside, 600_00), Ct);
        Undo.NextUndo.ShouldBe(LedgerAction.SetTarget);
        var funded = await Budget.FundTargetsAsync(Aug, Ct);
        funded.Funded.Amount.ShouldBe(200_00);
        Undo.NextUndo.ShouldBe(LedgerAction.FundTargets);
        await Undo.UndoAsync(Ct);
        Row(await Budget.GetMonthAsync(Aug, Ct), groceries).Assigned.Amount.ShouldBe(400_00);

        await Undo.UndoAsync(Ct);   // the target
        (await Budget.GetTargetAsync(groceries, Ct)).ShouldBeNull();
        await Undo.RedoAsync(Ct);
        (await Budget.GetTargetAsync(groceries, Ct)).ShouldBe(new TargetDto(groceries, TargetType.MonthlySetAside, 600_00));

        await Budget.DeleteTargetAsync(groceries, Ct);
        Undo.NextUndo.ShouldBe(LedgerAction.DeleteTarget);
        await Undo.UndoAsync(Ct);
        (await Budget.GetTargetAsync(groceries, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Loaded_ledger_data_gives_the_same_numbers_and_sees_new_assignments()
    {
        var (_, visa, groceries, rent) = await Example647Async();
        var ledger = await Budget.LoadLedgerAsync(Aug, Sep, Ct);
        ledger.Covers(Aug).ShouldBeTrue();
        ledger.Covers(new DateOnly(2026, 10, 1)).ShouldBeFalse();

        var fromLedger = await Budget.GetRangeAsync(ledger, Aug, Sep, Ct);
        var direct = await Budget.GetRangeAsync(Aug, Sep, Ct);
        fromLedger.Count.ShouldBe(2);
        for (var i = 0; i < 2; i++)
        {
            fromLedger[i].ReadyToAssign.ShouldBe(direct[i].ReadyToAssign);
            fromLedger[i].Groups.SelectMany(g => g.Categories).ShouldBe(direct[i].Groups.SelectMany(g => g.Categories));
        }

        var pay = Row(fromLedger[0], fromLedger[0].Groups.SelectMany(g => g.Categories).Single(c => c.CardPayment?.CardAccountId == visa).Id);
        pay.Available.Amount.ShouldBe(300_00);
        pay.CardPayment!.Difference.Amount.ShouldBe(-50_00);

        // An assignment after loading is visible without reloading the ledger.
        await Budget.AssignAsync(groceries, Aug, 450_00, Ct);
        var after = await Budget.GetRangeAsync(ledger, Aug, Aug, Ct);
        after[0].ReadyToAssign.Amount.ShouldBe(1_050_00);
        Row(after[0], groceries).Available.Amount.ShouldBe(0);

        (await Budget.ExplainAsync(ledger, rent, Aug, Ct)).ShouldBeEquivalentTo(await Budget.ExplainAsync(rent, Aug, Ct));
        (await Budget.ExplainAsync(ledger, null, Aug, Ct)).Total.Amount.ShouldBe(1_050_00);
        (await Budget.GetQuickAssignAsync(ledger, groceries, Sep, Ct)).ShouldBe(await Budget.GetQuickAssignAsync(groceries, Sep, Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Budget.GetRangeAsync(ledger, Aug, new DateOnly(2026, 12, 1), Ct));
    }

    [Fact]
    public async Task Month_notes_are_stored_audited_and_undoable()
    {
        (await Budget.GetMonthNoteAsync(Aug, Ct)).ShouldBeNull();
        _host.Bus.Messages.Clear();
        await Budget.SetMonthNoteAsync(new DateOnly(2026, 8, 20), "  Car insurance due  ", Ct);
        (await Budget.GetMonthNoteAsync(Aug, Ct)).ShouldBe("Car insurance due");
        _host.Bus.Messages.ShouldHaveSingleItem().ShouldBeOfType<BudgetChanged>().Months.ShouldBe([Aug]);
        Undo.NextUndo.ShouldBe(LedgerAction.EditMonthNote);

        await Budget.SetMonthNoteAsync(Aug, "Car insurance paid", Ct);
        await Budget.SetMonthNoteAsync(Aug, "Car insurance paid", Ct);    // unchanged: no-op
        _host.Bus.Messages.Clear();
        await Undo.UndoAsync(Ct);
        (await Budget.GetMonthNoteAsync(Aug, Ct)).ShouldBe("Car insurance due");
        _host.Bus.Messages.ShouldHaveSingleItem().ShouldBeOfType<BudgetChanged>().Months.ShouldBe([Aug]);

        await Budget.SetMonthNoteAsync(Aug, " ", Ct);
        (await Budget.GetMonthNoteAsync(Aug, Ct)).ShouldBeNull();
        (await Budget.GetMonthNoteAsync(Sep, Ct)).ShouldBeNull();
        await using var db = _host.Db();
        (await db.AuditEvents.CountAsync(e => e.EntityType == "Setting" && e.EntityId == "budget.monthNote.2026-08")).ShouldBe(4);   // create, update, the undo replay, delete
    }
}
