using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Debt;
using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Debt;
using Keel.Infrastructure.Tests.Ledger;

namespace Keel.Infrastructure.Tests.Debt;

public sealed class DebtPayoffServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateOnly Opening = new(2026, 8, 1);
    private static readonly NewPaymentCategory Names = new("Debt payments", "{0} payment");

    private static Task<AccountDto> Debt(LedgerTestHost host, string name, AccountType type, long owed, DebtTerms? terms) =>
        host.Accounts.CreateAccountAsync(new CreateAccountRequest(name, type, "USD", Opening, -owed, Debt: terms), Ct);

    private static Task Transfer(LedgerTestHost host, Guid from, Guid to, long amount, DateOnly date, Guid? category = null) =>
        host.Transactions.SaveAsync(new SaveTransactionRequest(null, from, date, -amount, null, category, null, TransferAccountId: to), Ct);

    [Fact]
    public async Task Debt_terms_are_saved_edited_cleared_and_undone_on_liability_accounts_only()
    {
        await using var host = await LedgerTestHost.CreateAsync();
        var visa = await Debt(host, "Visa", AccountType.CreditCard, 1_000_00, new DebtTerms(1999, 35_00));
        visa.InterestRateBps.ShouldBe(1999);
        visa.MinimumPayment.ShouldBe(35_00);
        visa.CanHaveDebtTerms.ShouldBeTrue();

        // Null leaves the terms alone (a rename); None clears them; each edit is one undoable action.
        (await host.Accounts.UpdateAccountAsync(new UpdateAccountRequest(visa.Id, "Visa Gold", true, null), Ct)).InterestRateBps.ShouldBe(1999);
        var edited = await host.Accounts.UpdateAccountAsync(new UpdateAccountRequest(visa.Id, "Visa Gold", true, null, new DebtTerms(2499, null)), Ct);
        edited.InterestRateBps.ShouldBe(2499);
        edited.MinimumPayment.ShouldBeNull();
        (await host.Undo.UndoAsync(Ct)).ShouldBe(LedgerAction.UpdateAccount);
        var restored = (await host.Accounts.GetAccountAsync(visa.Id, Ct))!;
        restored.InterestRateBps.ShouldBe(1999);
        restored.MinimumPayment.ShouldBe(35_00);
        (await host.Accounts.UpdateAccountAsync(new UpdateAccountRequest(visa.Id, "Visa", true, null, DebtTerms.None), Ct)).InterestRateBps.ShouldBeNull();

        // Asset accounts ignore terms; out-of-range values are refused.
        var checking = await host.Accounts.CreateAccountAsync(new CreateAccountRequest("Checking", AccountType.Checking, "USD", Opening, 10_00, Debt: new DebtTerms(500, 10_00)), Ct);
        checking.InterestRateBps.ShouldBeNull();
        checking.CanHaveDebtTerms.ShouldBeFalse();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => host.Accounts.UpdateAccountAsync(new UpdateAccountRequest(visa.Id, "Visa", true, null, new DebtTerms(10_001, 1)), Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => host.Accounts.UpdateAccountAsync(new UpdateAccountRequest(visa.Id, "Visa", true, null, new DebtTerms(1, -1)), Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Debt(host, "Bad", AccountType.Loan, 1_00, new DebtTerms(-1, 1)));
    }

    [Fact]
    public async Task The_plan_feeds_the_calculator_from_owed_liability_accounts()
    {
        await using var host = await LedgerTestHost.CreateAsync();
        var checking = await host.CheckingAsync(opening: 20_000_00);
        var visa = await Debt(host, "Visa", AccountType.CreditCard, 2_000_00, new DebtTerms(2400, 60_00));
        var car = await Debt(host, "Car loan", AccountType.Loan, 9_000_00, new DebtTerms(649, 250_00));
        var store = await Debt(host, "Store card", AccountType.CreditCard, 400_00, new DebtTerms(2699, null));
        await Debt(host, "Paid card", AccountType.CreditCard, 0, new DebtTerms(1999, 25_00));
        var closed = await Debt(host, "Old loan", AccountType.Loan, 0, new DebtTerms(500, 50_00));
        await host.Accounts.CloseAccountAsync(closed.Id, closeWithBalance: false, Ct);
        await host.AccountAsync("Brokerage", AccountType.Investment, 5_000_00);

        // Car payments are a categorized transfer to the tracking loan; a snapshot then later activity.
        var carPayment = await host.CategoryAsync("Car payment", "Bills");
        await Transfer(host, checking.Id, car.Id, 250_00, new DateOnly(2026, 8, 10), carPayment);
        await host.Snapshots.RecordAsync(car.Id, new DateOnly(2026, 8, 20), -8_600_00, BalanceSource.Manual, Ct);
        await Transfer(host, checking.Id, car.Id, 250_00, new DateOnly(2026, 8, 25), carPayment);

        var service = host.Get<IDebtPayoffService>();
        var overview = await service.GetPlanAsync(100_00, DebtOrdering.Avalanche, Ct);

        overview.Currency.ShouldBe("USD");
        overview.StartMonth.Day.ShouldBe(1);
        overview.Debts.Select(d => d.Name).ShouldBe(["Visa", "Car loan"]); // avalanche order
        overview.MissingTerms.ShouldHaveSingleItem().AccountId.ShouldBe(store.Id);
        var visaDebt = overview.Debts[0];
        visaDebt.Owed.ShouldBe(2_000_00);
        visaDebt.PaymentCategoryName.ShouldBe("Visa"); // its Credit Card Payment category
        var carDebt = overview.Debts[1];
        carDebt.Owed.ShouldBe(8_600_00 - 250_00);        // snapshot plus activity after it
        carDebt.PaymentCategoryId.ShouldBe(carPayment);   // the category of transfers into the loan
        carDebt.CurrentTarget.ShouldBeNull();

        var expected = DebtPayoffCalculator.Plan([visaDebt.ToInput(), carDebt.ToInput()], 100_00, DebtOrdering.Avalanche);
        overview.Plan.ShouldBeEquivalentTo(expected);
        overview.MinimumOnly.ShouldBeEquivalentTo(DebtPayoffCalculator.MinimumOnly([visaDebt.ToInput(), carDebt.ToInput()]));
        overview.Plan.Debts[0].FirstPayment.ShouldBe(160_00);
        overview.Plan.Debts[1].FirstPayment.ShouldBe(250_00);
        overview.MonthOf(overview.Plan.PayoffMonths!.Value).ShouldBe(overview.StartMonth.AddMonths(overview.Plan.PayoffMonths.Value - 1));

        (await service.GetPlanAsync(100_00, DebtOrdering.Snowball, Ct)).Debts.Select(d => d.Name).ShouldBe(["Visa", "Car loan"]);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => service.GetPlanAsync(-1, DebtOrdering.Snowball, Ct));
    }

    [Fact]
    public async Task Setting_payment_targets_is_one_undoable_budget_action_and_creates_missing_categories()
    {
        await using var host = await LedgerTestHost.CreateAsync();
        await host.CheckingAsync(opening: 20_000_00);
        var visa = await Debt(host, "Visa", AccountType.CreditCard, 2_000_00, new DebtTerms(2400, 60_00));
        var loan = await Debt(host, "Student loan", AccountType.Loan, 12_000_00, new DebtTerms(450, 130_00));
        var service = host.Get<IDebtPayoffService>();
        var budget = host.Get<IBudgetService>();

        var result = await service.SetPaymentTargetsAsync(90_00, DebtOrdering.Snowball, Names, Ct);
        result.ShouldBe(new DebtTargetsResult(2, 1));

        var overview = await service.GetPlanAsync(90_00, DebtOrdering.Snowball, Ct);
        var visaDebt = overview.Debts.Single(d => d.AccountId == visa.Id);
        var loanDebt = overview.Debts.Single(d => d.AccountId == loan.Id);
        loanDebt.PaymentCategoryName.ShouldBe("Student loan payment");
        (await host.Categories.GetCategoriesAsync(false, Ct)).Single(c => c.Id == loanDebt.PaymentCategoryId).GroupName.ShouldBe("Debt payments");
        visaDebt.CurrentTarget.ShouldBe(150_00);  // snowball: the smaller Visa gets the extra
        loanDebt.CurrentTarget.ShouldBe(130_00);
        var target = (await budget.GetTargetAsync(visaDebt.PaymentCategoryId!.Value, Ct))!;
        target.ShouldBe(new TargetDto(visaDebt.PaymentCategoryId.Value, TargetType.DebtPayment, 150_00, null, visa.Id));

        // Both targets go in one undo step; the created category stays (its own step).
        host.Undo.NextUndo.ShouldBe(LedgerAction.SetTarget);
        await host.Undo.UndoAsync(Ct);
        (await budget.GetTargetAsync(visaDebt.PaymentCategoryId.Value, Ct)).ShouldBeNull();
        (await budget.GetTargetAsync(loanDebt.PaymentCategoryId!.Value, Ct)).ShouldBeNull();
        host.Undo.NextUndo.ShouldBe(LedgerAction.CreateCategory);

        // Applying again reuses the created category and, through its target, finds it without a transfer.
        var again = await service.SetPaymentTargetsAsync(0, DebtOrdering.Avalanche, Names, Ct);
        again.ShouldBe(new DebtTargetsResult(2, 0));
        var after = await service.GetPlanAsync(0, DebtOrdering.Avalanche, Ct);
        after.Debts.Single(d => d.AccountId == loan.Id).PaymentCategoryId.ShouldBe(loanDebt.PaymentCategoryId);
        after.Debts.Single(d => d.AccountId == visa.Id).CurrentTarget.ShouldBe(60_00);
        (await host.Categories.GetCategoriesAsync(false, Ct)).Count(c => c.Name == "Student loan payment").ShouldBe(1);
    }

    [Fact]
    public async Task No_debts_means_an_empty_plan()
    {
        await using var host = await LedgerTestHost.CreateAsync();
        await host.CheckingAsync();
        var overview = await host.Get<IDebtPayoffService>().GetPlanAsync(50_00, DebtOrdering.Avalanche, Ct);
        overview.HasDebts.ShouldBeFalse();
        overview.Plan.Debts.ShouldBeEmpty();
        overview.Plan.PayoffMonths.ShouldBe(0);
        (await host.Get<IDebtPayoffService>().SetPaymentTargetsAsync(50_00, DebtOrdering.Avalanche, Names, Ct)).ShouldBe(new DebtTargetsResult(0, 0));
    }
}
