using Keel.Application.Budget;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Ledger;

/// <summary>F-BUD-1 category management, F-BUD-7 notes and F-BUD-8 templates.</summary>
public sealed class CategoryManagementTests : IAsyncLifetime
{
    private static readonly DateOnly Aug = new(2026, 8, 1);
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    private ICategoryService Categories => _host.Categories;

    private IBudgetService Budget => _host.Get<IBudgetService>();

    private async Task<CategoryGroupDto> GroupAsync(string name) => (await Categories.GetGroupsAsync(Ct)).Single(g => g.Name == name);

    [Fact]
    public async Task Create_rename_hide_reorder_and_move()
    {
        var bills = await Categories.CreateGroupAsync("Bills", Ct);
        var fun = await Categories.CreateGroupAsync("Fun", Ct);
        var rent = await Categories.CreateCategoryAsync(bills.Id, "Rent", Ct);
        var power = await Categories.CreateCategoryAsync(bills.Id, "Power", Ct);
        var games = await Categories.CreateCategoryAsync(fun.Id, "Games", Ct);
        _host.Undo.NextUndo.ShouldBe(LedgerAction.CreateCategory);

        var groups = await Categories.GetGroupsAsync(Ct);
        groups.Select(g => g.Name).ShouldBe(["Credit Card Payments", "Bills", "Fun"]);
        groups[0].IsSystem.ShouldBeTrue();
        groups[1].Categories.Select(c => c.Name).ShouldBe(["Rent", "Power"]);

        await Categories.RenameGroupAsync(fun.Id, "  Fun   money ", Ct);
        await Categories.RenameCategoryAsync(power.Id, "Electricity", Ct);
        await Categories.SetCategoryHiddenAsync(games.Id, true, Ct);
        await Categories.SetGroupHiddenAsync(bills.Id, true, Ct);
        await Categories.ReorderGroupsAsync([fun.Id, groups[0].Id, bills.Id], Ct);
        await Categories.MoveCategoryAsync(power.Id, fun.Id, 0, Ct);
        await Categories.MoveCategoryAsync(rent.Id, bills.Id, 5, Ct);

        groups = await Categories.GetGroupsAsync(Ct);
        groups.Select(g => g.Name).ShouldBe(["Fun money", "Credit Card Payments", "Bills"]);
        groups[0].Categories.Select(c => (c.Name, c.IsHidden)).ShouldBe([("Electricity", false), ("Games", true)]);
        groups[2].IsHidden.ShouldBeTrue();
        groups[2].Categories.Select(c => c.Name).ShouldBe(["Rent"]);
        (await Categories.GetCategoriesAsync(includeHidden: false, Ct)).Select(c => c.Name).ShouldBe(["Ready to Assign", "Electricity"]);

        await _host.Undo.UndoAsync(Ct);   // move rent (no-op order) is still an action
        await _host.Undo.UndoAsync(Ct);   // move power back
        (await GroupAsync("Bills")).Categories.Select(c => c.Name).ShouldBe(["Rent", "Electricity"]);
        await Should.ThrowAsync<ArgumentException>(() => Categories.ReorderGroupsAsync([fun.Id], Ct));
        await Should.ThrowAsync<LedgerValidationException>(() => Categories.RenameCategoryAsync(rent.Id, "  ", Ct));
    }

    [Fact]
    public async Task System_groups_and_categories_are_protected()
    {
        var visa = await _host.AccountAsync("Visa", AccountType.CreditCard);
        var cards = await GroupAsync("Credit Card Payments");
        var payVisa = cards.Categories.Single();
        payVisa.LinkedAccountId.ShouldBe(visa.Id);

        async Task Refused(Func<Task> action) =>
            (await Should.ThrowAsync<LedgerValidationException>(action)).Error.ShouldBe(LedgerError.SystemCategoryProtected);

        await Refused(() => Categories.RenameGroupAsync(cards.Id, "X", Ct));
        await Refused(() => Categories.SetGroupHiddenAsync(cards.Id, true, Ct));
        await Refused(() => Categories.DeleteGroupAsync(cards.Id, null, Ct));
        await Refused(() => Categories.CreateCategoryAsync(cards.Id, "Extra", Ct));
        await Refused(() => Categories.RenameCategoryAsync(payVisa.Id, "X", Ct));
        await Refused(() => Categories.DeleteCategoryAsync(payVisa.Id, null, Ct));
        await Refused(() => Categories.SetCategoryHiddenAsync(SystemIds.ReadyToAssignCategory, true, Ct));
        await Refused(() => Categories.DeleteGroupAsync(SystemIds.InflowGroup, null, Ct));
        (await Should.ThrowAsync<LedgerValidationException>(() => Categories.RenameGroupAsync(Guid.NewGuid(), "X", Ct))).Error.ShouldBe(LedgerError.CategoryGroupNotFound);
    }

    [Fact]
    public async Task Deleting_a_category_with_history_moves_it_to_the_replacement_and_undo_restores_it()
    {
        var checking = await _host.CheckingAsync(opening: 0);
        var dining = await _host.CategoryAsync("Dining");
        var food = await _host.CategoryAsync("Food");
        var unused = await _host.CategoryAsync("Unused");
        await _host.AddAsync(checking.Id, 1_000_00, "Employer", SystemIds.ReadyToAssignCategory, Aug);
        var plain = await _host.AddAsync(checking.Id, -30_00, "Cafe", dining);
        var deleted = await _host.AddAsync(checking.Id, -5_00, "Cafe", dining);
        await _host.Transactions.DeleteAsync([deleted.Id], Ct);
        var split = await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 12), -50_00, "Market", null, null,
            Splits: [new SplitLine(dining, null, -20_00), new SplitLine(food, null, -30_00)]), Ct);
        await Budget.AssignAsync(dining, Aug, 100_00, Ct);
        await Budget.AssignAsync(food, Aug, 40_00, Ct);
        await Budget.AssignAsync(dining, Aug.AddMonths(1), 25_00, Ct);
        await Budget.SetTargetAsync(new TargetDto(dining, TargetType.MonthlySetAside, 100_00), Ct);
        var rtaBefore = (await Budget.GetMonthAsync(Aug, Ct)).ReadyToAssign;

        (await Categories.GetUsageAsync(dining, Ct)).ShouldBe(new CategoryUsageDto(3, 2, 0, true));
        (await Categories.GetUsageAsync(unused, Ct)).HasHistory.ShouldBeFalse();
        (await Should.ThrowAsync<LedgerValidationException>(() => Categories.DeleteCategoryAsync(dining, null, Ct))).Error.ShouldBe(LedgerError.ReplacementCategoryRequired);
        (await Should.ThrowAsync<LedgerValidationException>(() => Categories.DeleteCategoryAsync(dining, dining, Ct))).Error.ShouldBe(LedgerError.InvalidReplacementCategory);
        (await Should.ThrowAsync<LedgerValidationException>(() => Categories.DeleteCategoryAsync(dining, SystemIds.ReadyToAssignCategory, Ct))).Error.ShouldBe(LedgerError.InvalidReplacementCategory);

        _host.Bus.Messages.Clear();
        await Categories.DeleteCategoryAsync(dining, food, Ct);
        _host.Bus.LedgerChanges.ShouldHaveSingleItem().AccountIds.ShouldBe([checking.Id]);
        _host.Bus.Messages.OfType<BudgetChanged>().ShouldHaveSingleItem();

        await using (var db = _host.Db())
        {
            (await db.Categories.AnyAsync(c => c.Id == dining)).ShouldBeFalse();
            (await db.Transactions.IgnoreQueryFilters().SingleAsync(t => t.Id == plain.Id)).CategoryId.ShouldBe(food);
            (await db.Transactions.IgnoreQueryFilters().SingleAsync(t => t.Id == deleted.Id)).CategoryId.ShouldBe(food);
            (await db.TransactionSplits.Where(s => s.TransactionId == split.Id).Select(s => s.CategoryId).ToListAsync()).ShouldAllBe(c => c == food);
            (await db.BudgetAssignments.Where(a => a.CategoryId == food).OrderBy(a => a.Month).Select(a => a.Assigned).ToListAsync()).ShouldBe([140_00, 25_00]);
            (await db.Targets.AnyAsync()).ShouldBeFalse();
        }

        var aug = await Budget.GetMonthAsync(Aug, Ct);
        aug.ReadyToAssign.ShouldBe(rtaBefore);
        aug.Groups.SelectMany(g => g.Categories).Single(c => c.Id == food).Activity.Amount.ShouldBe(-80_00);

        await Categories.DeleteCategoryAsync(unused, null, Ct);   // no history: no replacement needed
        await _host.Undo.UndoAsync(Ct);
        (await _host.Undo.UndoAsync(Ct)).ShouldBe(LedgerAction.DeleteCategory);
        await using (var db = _host.Db())
        {
            (await db.Transactions.IgnoreQueryFilters().SingleAsync(t => t.Id == plain.Id)).CategoryId.ShouldBe(dining);
            (await db.BudgetAssignments.Where(a => a.CategoryId == dining).CountAsync()).ShouldBe(2);
            (await db.BudgetAssignments.SingleAsync(a => a.CategoryId == food)).Assigned.ShouldBe(40_00);
            (await db.Targets.SingleAsync()).CategoryId.ShouldBe(dining);
        }

        var groceries = (await Categories.GetCategoriesAsync(false, Ct)).Single(c => c.Id == food);
        (await Budget.GetMonthAsync(Aug, Ct)).Groups.SelectMany(g => g.Categories).Single(c => c.Id == groceries.Id).Activity.Amount.ShouldBe(-30_00);
    }

    [Fact]
    public async Task Deleting_a_group_needs_a_replacement_outside_it()
    {
        var checking = await _host.CheckingAsync(opening: 0);
        var old = await Categories.CreateGroupAsync("Old", Ct);
        var a = await Categories.CreateCategoryAsync(old.Id, "A", Ct);
        var b = await Categories.CreateCategoryAsync(old.Id, "B", Ct);
        var keep = await _host.CategoryAsync("Keep");
        await _host.AddAsync(checking.Id, -10_00, "Shop", a.Id);
        await Budget.AssignAsync(b.Id, Aug, 5_00, Ct);

        (await Should.ThrowAsync<LedgerValidationException>(() => Categories.DeleteGroupAsync(old.Id, null, Ct))).Error.ShouldBe(LedgerError.ReplacementCategoryRequired);
        (await Should.ThrowAsync<LedgerValidationException>(() => Categories.DeleteGroupAsync(old.Id, b.Id, Ct))).Error.ShouldBe(LedgerError.InvalidReplacementCategory);
        await Categories.DeleteGroupAsync(old.Id, keep, Ct);

        (await Categories.GetGroupsAsync(Ct)).Select(g => g.Name).ShouldNotContain("Old");
        var aug = await Budget.GetMonthAsync(Aug, Ct);
        var row = aug.Groups.SelectMany(g => g.Categories).Single(c => c.Id == keep);
        (row.Activity.Amount, row.Assigned.Amount).ShouldBe((-10_00, 5_00));

        var empty = await Categories.CreateGroupAsync("Empty", Ct);
        await Categories.DeleteGroupAsync(empty.Id, null, Ct);
        _host.Undo.NextUndo.ShouldBe(LedgerAction.DeleteCategoryGroup);
    }

    [Fact]
    public async Task Notes_and_templates()
    {
        var rent = await _host.CategoryAsync("Rent", "Bills");
        (await Categories.GetNoteAsync(rent, Ct)).ShouldBeNull();
        await Categories.SetNoteAsync(rent, "  due on the 1st ", Ct);
        (await Categories.GetNoteAsync(rent, Ct)).ShouldBe("due on the 1st");
        await Categories.SetNoteAsync(rent, " ", Ct);
        (await Categories.GetNoteAsync(rent, Ct)).ShouldBeNull();

        var template = new[]
        {
            new CategoryTemplateGroup("bills", ["Rent", "Internet"]),
            new CategoryTemplateGroup("Everyday", ["Groceries", "Dining out"]),
        };
        (await Categories.ApplyTemplateAsync(template, Ct)).ShouldBe(3);
        (await Categories.ApplyTemplateAsync(template, Ct)).ShouldBe(0);   // never duplicates, never deletes
        var groups = await Categories.GetGroupsAsync(Ct);
        groups.Single(g => g.Name == "Bills").Categories.Select(c => c.Name).ShouldBe(["Rent", "Internet"]);
        groups.Single(g => g.Name == "Everyday").Categories.Select(c => c.Name).ShouldBe(["Groceries", "Dining out"]);
        _host.Undo.NextUndo.ShouldBe(LedgerAction.ApplyCategoryTemplate);
    }
}
