using Keel.Application.Budget;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Ledger;

/// <summary>F-BUD-6: Flex-mode tags on categories (stored on <c>Category.FlexKind</c>) and the Flex summary of the budget service.</summary>
public sealed class FlexTagTests : IAsyncLifetime
{
    private static readonly DateOnly Aug = new(2026, 8, 1);
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    private IBudgetService Budget => _host.Get<IBudgetService>();

    [Fact]
    public async Task Tagging_is_stored_by_name_undoable_and_announced()
    {
        var rent = await _host.CategoryAsync("Rent", "Bills");
        await _host.Categories.SetFlexKindAsync(rent, FlexKind.Fixed, Ct);

        (await _host.Categories.GetCategoriesAsync(true, Ct)).Single(c => c.Id == rent).FlexKind.ShouldBe(FlexKind.Fixed);
        (await _host.Categories.GetGroupsAsync(Ct)).SelectMany(g => g.Categories).Single(c => c.Id == rent).FlexKind.ShouldBe(FlexKind.Fixed);
        _host.Undo.NextUndo.ShouldBe(LedgerAction.TagCategoryFlex);
        _host.Bus.Messages.OfType<LedgerChanged>().ShouldNotBeEmpty();

        await using (var db = _host.Db())
        {
            var stored = await db.Database.SqlQueryRaw<string>("SELECT \"FlexKind\" AS \"Value\" FROM \"Categories\" WHERE \"Id\" = {0}", rent).SingleAsync();
            stored.ShouldBe("Fixed");                                            // enums are stored by name
        }

        await _host.Undo.UndoAsync(Ct);
        (await _host.Categories.GetCategoriesAsync(true, Ct)).Single(c => c.Id == rent).FlexKind.ShouldBe(FlexKind.Unset);

        // Setting the same tag again changes nothing and adds no undo entry.
        await _host.Categories.SetFlexKindAsync(rent, FlexKind.Unset, Ct);
        _host.Undo.NextUndo.ShouldNotBe(LedgerAction.TagCategoryFlex);
    }

    [Fact]
    public async Task System_categories_cannot_be_tagged()
    {
        var visa = await _host.AccountAsync("Visa", AccountType.CreditCard);
        var pay = (await _host.Categories.GetCategoriesAsync(true, Ct)).Single(c => c.LinkedAccountId == visa.Id);
        (await Should.ThrowAsync<LedgerValidationException>(() => _host.Categories.SetFlexKindAsync(pay.Id, FlexKind.Fixed, Ct))).Error
            .ShouldBe(LedgerError.SystemCategoryProtected);
        (await Should.ThrowAsync<LedgerValidationException>(() => _host.Categories.SetFlexKindAsync(SystemIds.ReadyToAssignCategory, FlexKind.Flex, Ct))).Error
            .ShouldBe(LedgerError.SystemCategoryProtected);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => _host.Categories.SetFlexKindAsync(pay.Id, (FlexKind)42, Ct));
    }

    [Fact]
    public async Task The_budget_month_carries_the_flex_summary_of_the_grid_numbers_with_defaults_from_targets()
    {
        var checking = await _host.CheckingAsync(opening: 0);
        var rent = await _host.CategoryAsync("Rent", "Bills");
        var insurance = await _host.CategoryAsync("Car insurance", "Bills");
        var groceries = await _host.CategoryAsync("Groceries");
        var dining = await _host.CategoryAsync("Dining");
        await _host.AddAsync(checking.Id, 3_000_00, "Employer", SystemIds.ReadyToAssignCategory, Aug);
        await Budget.AssignAsync(rent, Aug, 1_500_00, Ct);
        await Budget.AssignAsync(insurance, Aug, 100_00, Ct);
        await Budget.AssignAsync(groceries, Aug, 400_00, Ct);
        await _host.AddAsync(checking.Id, -1_500_00, "Landlord", rent, Aug.AddDays(4));
        await _host.AddAsync(checking.Id, -250_00, "Grocer", groceries, Aug.AddDays(9));
        await Budget.SetTargetAsync(new TargetDto(rent, TargetType.MonthlySetAside, 1_500_00), Ct);
        await Budget.SetTargetAsync(new TargetDto(insurance, TargetType.SavingsBalanceByDate, 1_200_00, new DateOnly(2027, 7, 1)), Ct);
        await Budget.SetTargetAsync(new TargetDto(dining, TargetType.MonthlySpending, 50_00), Ct);
        await _host.Categories.SetFlexKindAsync(dining, FlexKind.Flex, Ct);   // the tag beats the Fixed default of its target

        var month = await Budget.GetMonthAsync(Aug, Ct);
        var cells = month.Groups.SelectMany(g => g.Categories).ToDictionary(c => c.Id);
        (cells[rent].Flex, cells[rent].FlexTag).ShouldBe((FlexKind.Fixed, FlexKind.Unset));
        cells[insurance].Flex.ShouldBe(FlexKind.NonMonthly);
        cells[groceries].Flex.ShouldBe(FlexKind.Flex);
        (cells[dining].Flex, cells[dining].FlexTag).ShouldBe((FlexKind.Flex, FlexKind.Flex));

        var flex = month.Flex.ShouldNotBeNull();
        flex.Income.ShouldBe(3_000_00);
        flex.Fixed.CategoryIds.ShouldBe([rent]);
        flex.Fixed.Assigned.ShouldBe(1_500_00);
        flex.NonMonthly.Assigned.ShouldBe(100_00);
        flex.Flex.CategoryIds.ShouldBe([groceries, dining]);
        flex.Flex.Assigned.ShouldBe(cells[groceries].Assigned.Amount + cells[dining].Assigned.Amount);
        flex.Flex.Available.ShouldBe(cells[groceries].Available.Amount + cells[dining].Available.Amount);
        flex.Flex.Spent.ShouldBe(250_00);

        // The loaded-ledger overload (the Budget screen's path) gives the same summary.
        var ledger = await Budget.LoadLedgerAsync(Aug, Aug, Ct);
        var screen = (await Budget.GetRangeAsync(ledger, Aug, Aug, Ct)).Single();
        screen.Flex.ShouldBe(flex, new FlexSummaryComparer());
    }

    private sealed class FlexSummaryComparer : IEqualityComparer<FlexSummary?>
    {
        public bool Equals(FlexSummary? x, FlexSummary? y) => x is not null && y is not null && x.Month == y.Month && x.Income == y.Income
            && Same(x.Fixed, y.Fixed) && Same(x.NonMonthly, y.NonMonthly) && Same(x.Flex, y.Flex) && x.CardPaymentCategoryIds.SequenceEqual(y.CardPaymentCategoryIds);

        public int GetHashCode(FlexSummary? obj) => obj?.Income.GetHashCode() ?? 0;

        private static bool Same(FlexBucket a, FlexBucket b) =>
            (a.Kind, a.Carry, a.Assigned, a.Activity, a.Available) == (b.Kind, b.Carry, b.Assigned, b.Activity, b.Available) && a.CategoryIds.SequenceEqual(b.CategoryIds);
    }
}
