using Keel.Domain.Budgeting;
using static Keel.Domain.Tests.Budgeting.BudgetBuilder;
using static VerifyXunit.Verifier;

namespace Keel.Domain.Tests.Budgeting;

/// <summary>Golden edge cases around PRD 6.4 (M2 exit criteria). Assertions are the spec; Verify records the rest.</summary>
public class EdgeCaseTests
{
    [Fact]
    public Task Refund_on_a_card_larger_than_that_months_spend_is_floored_at_zero()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var visa = b.Account("Visa", AccountType.CreditCard);
        var groceries = b.Category("Groceries");
        var pay = b.PaymentCategory(visa);
        b.Txn("2026-07-01", checking, 1_000_00, b.Rta)
            .Assign(groceries, "2026-07", 80_00)
            .Txn("2026-07-10", visa, -80_00, groceries)     // covered: 80 moves to Pay_Visa
            .Txn("2026-08-03", visa, -30_00, groceries)
            .Txn("2026-08-20", visa, 80_00, groceries);     // refund of July, larger than August spend

        var snapshot = b.Compute("2026-07", "2026-08");
        var aug = snapshot.Month(M("2026-08"));
        var g = aug.Category(groceries);
        g.Activity.ShouldBe(50_00);                        // refunds are netted inside CardActivity
        g.CreditSpending.ShouldBe(0);                      // CardSpend = max(0, −50) = 0
        g.Covered.ShouldBe(0);
        g.CardSpendByCard.ShouldBeEmpty();
        g.Available.ShouldBe(50_00);                       // the refund lands in the category
        aug.Category(pay).Covered.ShouldBe(0);
        aug.Category(pay).Available.ShouldBe(80_00);       // not reduced: surplus over the 30 owed stays available (6.4.5)
        aug.ReadyToAssign.ShouldBe(920_00);

        return Verify(SnapshotPrinter.Print(snapshot, b, (groceries, "2026-08"), (pay, "2026-08")));
    }

    [Fact]
    public Task Overspending_on_two_cards_is_allocated_proportionally_with_the_rounding_remainder_on_the_largest_card()
    {
        var b = new BudgetBuilder();
        var visa = b.Account("Visa", AccountType.CreditCard);
        var amex = b.Account("Amex", AccountType.CreditCard);
        var dining = b.Category("Dining");
        b.Assign(dining, "2026-08", 100_00)
            .Txn("2026-08-05", amex, -50_00, dining)
            .Txn("2026-08-06", visa, -70_01, dining);

        var snapshot = b.Compute("2026-08", "2026-08");
        var d = snapshot.Cell(dining, M("2026-08"));
        d.Available.ShouldBe(-20_01);
        d.CreditSpending.ShouldBe(120_01);
        d.CreditOverspent.ShouldBe(-20_01);
        d.CashOverspent.ShouldBe(0);

        // Uncovered 20.01 split 70.01 : 50.00 → 11.6732… and 8.3367…; floors 11.67 + 8.33 = 20.00,
        // the remaining cent goes to the largest card (Visa).
        d.CoveredByCard.ShouldBe([new AccountAmount(visa, 70_01 - 11_68), new AccountAmount(amex, 50_00 - 8_33)]);
        d.Covered.ShouldBe(100_00);
        snapshot.Cell(b.PaymentCategory(visa), M("2026-08")).Available.ShouldBe(58_33);
        snapshot.Cell(b.PaymentCategory(amex), M("2026-08")).Available.ShouldBe(41_67);

        return Verify(SnapshotPrinter.Print(snapshot, b, (dining, "2026-08"), (b.PaymentCategory(visa), "2026-08"), (b.PaymentCategory(amex), "2026-08")));
    }

    [Fact]
    public void Rounding_ties_go_to_the_first_account_and_spill_so_covered_stays_within_card_spend()
    {
        var b = new BudgetBuilder();
        var visa = b.Account("Visa", AccountType.CreditCard);
        var amex = b.Account("Amex", AccountType.CreditCard);
        var discover = b.Account("Discover", AccountType.CreditCard);
        var dining = b.Category("Dining");
        var fun = b.Category("Fun");

        // Tie: uncovered 0.01 over two equal 0.01 spends: the uncovered cent goes to the first card.
        b.Assign(dining, "2026-08", 1)
            .Txn("2026-08-05", amex, -1, dining)
            .Txn("2026-08-05", visa, -1, dining);

        // Spill: uncovered 0.02 over three 0.01 spends: floors are all 0, the first card can absorb
        // only one cent, the second takes the other.
        b.Assign(fun, "2026-08", 1)
            .Txn("2026-08-05", visa, -1, fun)
            .Txn("2026-08-05", amex, -1, fun)
            .Txn("2026-08-05", discover, -1, fun);

        var month = b.Compute("2026-08", "2026-08").Month(M("2026-08"));
        month.Category(dining).CoveredByCard.ShouldBe([new AccountAmount(visa, 0), new AccountAmount(amex, 1)]);
        month.Category(fun).CoveredByCard.ShouldBe([new AccountAmount(visa, 0), new AccountAmount(amex, 0), new AccountAmount(discover, 1)]);
    }

    [Fact]
    public Task Card_payment_larger_than_the_covered_amount_is_cash_overspending_of_the_payment_category()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var visa = b.Account("Visa", AccountType.CreditCard);
        var dining = b.Category("Dining");
        var groceries = b.Category("Groceries");
        var pay = b.PaymentCategory(visa);
        b.Txn("2026-07-01", checking, 1_000_00, b.Rta)
            .Txn("2026-07-15", visa, -150_00, dining)        // never budgeted: credit overspending, debt stays uncovered
            .Assign(groceries, "2026-08", 100_00)
            .Txn("2026-08-10", visa, -100_00, groceries)     // covered
            .Transfer("2026-08-28", checking, visa, 250_00); // pays the covered 100 and the old 150

        var snapshot = b.Compute("2026-07", "2026-09");
        snapshot.Cell(dining, M("2026-07")).Overspending.ShouldBe(BudgetOverspending.Credit);
        var aug = snapshot.Month(M("2026-08"));
        aug.Category(pay).Covered.ShouldBe(100_00);
        aug.Category(pay).Payments.ShouldBe(250_00);
        aug.Category(pay).Activity.ShouldBe(-150_00);
        aug.Category(pay).Available.ShouldBe(-150_00);
        aug.Category(pay).CashOverspent.ShouldBe(-150_00);
        aug.Category(pay).Overspending.ShouldBe(BudgetOverspending.Cash);
        aug.ReadyToAssign.ShouldBe(900_00);
        var sep = snapshot.Month(M("2026-09"));
        sep.ReadyToAssign.ShouldBe(750_00);               // the unbudgeted 150 came out of RTA
        sep.Category(pay).Carry.ShouldBe(0);

        return Verify(SnapshotPrinter.Print(snapshot, b, (pay, "2026-08")));
    }

    [Fact]
    public Task Cash_advance_is_categorized_on_both_sides_and_flows_through_the_payment_category()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var visa = b.Account("Visa", AccountType.CreditCard);
        var advance = b.Category("Cash Advance");
        var pay = b.PaymentCategory(visa);
        b.Txn("2026-08-01", checking, 1_000_00, b.Rta)
            .Transfer("2026-08-10", visa, checking, 200_00, fromCategory: advance, toCategory: b.Rta)
            .Assign(advance, "2026-08", 150_00)
            .Transfer("2026-08-20", visa, checking, 50_00);   // an uncategorized advance changes nothing

        var aug = b.Compute("2026-08", "2026-08").Month(M("2026-08"));
        aug.InflowThisMonth.ShouldBe(1_200_00);
        aug.ReadyToAssign.ShouldBe(1_050_00);
        aug.UncategorizedActivity.ShouldBe(0);
        var a = aug.Category(advance);
        a.Activity.ShouldBe(-200_00);
        a.CreditOverspent.ShouldBe(-50_00);
        a.CashOverspent.ShouldBe(0);
        a.Covered.ShouldBe(150_00);
        aug.Category(pay).Available.ShouldBe(150_00);
        aug.Category(pay).Payments.ShouldBe(0);

        return Verify(SnapshotPrinter.Print(b.Compute("2026-08", "2026-08"), b, (advance, "2026-08"), (pay, "2026-08")));
    }

    [Fact]
    public Task Category_with_activity_but_no_assignment()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var visa = b.Account("Visa", AccountType.CreditCard);
        var gifts = b.Category("Gifts");
        var hobbies = b.Category("Hobbies");
        b.Txn("2026-08-01", checking, 500_00, b.Rta)
            .Txn("2026-08-12", checking, -40_00, gifts)
            .Txn("2026-08-13", visa, -25_00, hobbies);

        var snapshot = b.Compute("2026-08", "2026-09");
        var aug = snapshot.Month(M("2026-08"));
        aug.Category(gifts).Available.ShouldBe(-40_00);
        aug.Category(gifts).Overspending.ShouldBe(BudgetOverspending.Cash);
        aug.Category(hobbies).Available.ShouldBe(-25_00);
        aug.Category(hobbies).Overspending.ShouldBe(BudgetOverspending.Credit);
        aug.Category(hobbies).Covered.ShouldBe(0);
        aug.Category(b.PaymentCategory(visa)).Available.ShouldBe(0);
        aug.ReadyToAssign.ShouldBe(500_00);
        snapshot.Month(M("2026-09")).ReadyToAssign.ShouldBe(460_00);

        return Verify(SnapshotPrinter.Print(snapshot, b));
    }

    [Fact]
    public Task Assignments_in_future_months_reduce_current_ready_to_assign()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var rent = b.Category("Rent");
        b.Txn("2026-08-01", checking, 1_000_00, b.Rta)
            .Assign(rent, "2026-08", 400_00)
            .Assign(rent, "2026-10", 400_00)
            .Assign(rent, "2027-01", 100_00);              // outside the computed range, still counted

        var snapshot = b.Compute("2026-08", "2026-10");
        var aug = snapshot.Month(M("2026-08"));
        aug.AssignedInFuture.ShouldBe(500_00);
        aug.ReadyToAssign.ShouldBe(100_00);
        snapshot.Month(M("2026-09")).ReadyToAssign.ShouldBe(100_00);
        snapshot.Month(M("2026-10")).ReadyToAssign.ShouldBe(100_00);
        snapshot.Month(M("2026-10")).AssignedInFuture.ShouldBe(100_00);
        snapshot.Cell(rent, M("2026-10")).Available.ShouldBe(1_200_00 - 400_00);

        return Verify(SnapshotPrinter.Print(snapshot, b));
    }

    [Fact]
    public Task Ready_to_assign_can_be_negative()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var rent = b.Category("Rent");
        b.Txn("2026-08-01", checking, 500_00, b.Rta)
            .Assign(rent, "2026-08", 800_00);

        var snapshot = b.Compute("2026-08", "2026-09");
        snapshot.Month(M("2026-08")).ReadyToAssign.ShouldBe(-300_00);
        snapshot.Month(M("2026-09")).ReadyToAssign.ShouldBe(-300_00);
        snapshot.Cell(rent, M("2026-09")).Available.ShouldBe(800_00);

        return Verify(SnapshotPrinter.Print(snapshot, b));
    }

    [Fact]
    public Task Overspending_spanning_cash_and_credit_in_the_same_month()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var visa = b.Account("Visa", AccountType.CreditCard);
        var dining = b.Category("Dining");
        b.Txn("2026-08-01", checking, 1_000_00, b.Rta)
            .Assign(dining, "2026-08", 100_00)
            .Txn("2026-08-10", checking, -120_00, dining)
            .Txn("2026-08-11", visa, -30_00, dining);

        var snapshot = b.Compute("2026-08", "2026-09");
        var d = snapshot.Cell(dining, M("2026-08"));
        d.Available.ShouldBe(-50_00);
        d.CreditOverspent.ShouldBe(-30_00);                // min(50, 30)
        d.CashOverspent.ShouldBe(-20_00);
        d.Overspending.ShouldBe(BudgetOverspending.Cash);
        d.Covered.ShouldBe(0);
        snapshot.Cell(b.PaymentCategory(visa), M("2026-08")).Available.ShouldBe(0);
        snapshot.Month(M("2026-09")).ReadyToAssign.ShouldBe(900_00 - 20_00);

        return Verify(SnapshotPrinter.Print(snapshot, b, (dining, "2026-08")));
    }

    [Fact]
    public Task Month_with_no_data_between_two_months_with_data()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var visa = b.Account("Visa", AccountType.CreditCard);
        var savings = b.Category("Vacation");
        var pay = b.PaymentCategory(visa);
        b.Txn("2026-08-01", checking, 1_000_00, b.Rta)
            .Assign(savings, "2026-08", 300_00)
            .Txn("2026-08-15", visa, -50_00, savings)
            .Txn("2026-10-05", checking, -100_00, savings)
            .Transfer("2026-10-20", checking, visa, 50_00);

        var snapshot = b.Compute("2026-08", "2026-10");
        snapshot.Cell(savings, M("2026-09")).Carry.ShouldBe(250_00);
        snapshot.Cell(savings, M("2026-09")).Available.ShouldBe(250_00);
        snapshot.Cell(savings, M("2026-10")).Available.ShouldBe(150_00);
        snapshot.Cell(pay, M("2026-09")).Available.ShouldBe(50_00);
        snapshot.Cell(pay, M("2026-10")).Available.ShouldBe(0);
        snapshot.Months.Select(m => m.ReadyToAssign).ShouldAllBe(rta => rta == 700_00);

        return Verify(SnapshotPrinter.Print(snapshot, b));
    }

    [Fact]
    public Task Category_hidden_mid_history_still_counts_but_leaves_group_rows()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var hobbies = b.Category("Hobbies");
        var books = b.Category("Books");
        var archive = b.Group("Archive", hidden: true);
        var old = b.Category("Old stuff", archive);
        b.Txn("2026-07-01", checking, 1_000_00, b.Rta)
            .Assign(hobbies, "2026-07", 100_00)
            .Assign(books, "2026-07", 50_00)
            .Assign(old, "2026-07", 20_00)
            .Txn("2026-08-10", checking, -30_00, hobbies);
        b.Hide(hobbies);                                  // hidden after its history was written

        var snapshot = b.Compute("2026-07", "2026-09");
        var sep = snapshot.Month(M("2026-09"));
        sep.Category(hobbies).IsVisible.ShouldBeFalse();
        sep.Category(hobbies).Available.ShouldBe(70_00);  // history still computed and carried
        sep.Category(old).IsVisible.ShouldBeFalse();      // hidden via its group
        sep.ReadyToAssign.ShouldBe(830_00);               // hidden assignments still reduce RTA
        sep.Groups.Single(g => g.GroupId == EverydayGroup).Available.ShouldBe(50_00);  // visible children only
        sep.Groups.Single(g => g.GroupId == archive).IsHidden.ShouldBeTrue();
        sep.Groups.Single(g => g.GroupId == archive).Available.ShouldBe(0);
        sep.TotalAvailable.ShouldBe(140_00);              // month totals include hidden categories

        return Verify(SnapshotPrinter.Print(snapshot, b));
    }

    [Fact]
    public Task Opening_balances_flow_to_ready_to_assign_and_tracking_balances_do_not()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var savings = b.Account("Savings", AccountType.Savings);
        var mortgage = b.Account("Mortgage", AccountType.Loan);
        var rent = b.Category("Rent");
        b.Txn("2026-08-15", checking, 2_500_00, b.Rta)    // Starting Balance, account opened mid-month
            .Txn("2026-09-01", savings, 1_000_00, b.Rta)  // Starting Balance
            .Txn("2026-08-15", mortgage, -250_000_00, null) // tracking Starting Balance is uncategorized
            .Assign(rent, "2026-08", 1_200_00);

        var snapshot = b.Compute("2026-07", "2026-09");
        snapshot.Month(M("2026-07")).ReadyToAssign.ShouldBe(-1_200_00);  // assigned in a future month
        snapshot.Month(M("2026-08")).ReadyToAssign.ShouldBe(1_300_00);
        snapshot.Month(M("2026-09")).ReadyToAssign.ShouldBe(2_300_00);
        snapshot.Months.ShouldAllBe(m => m.UncategorizedActivity == 0);

        return Verify(SnapshotPrinter.Print(snapshot, b));
    }

    [Fact]
    public Task Transfers_to_tracking_accounts_are_categorized_outflows_and_on_budget_transfers_are_ignored()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var savings = b.Account("Savings", AccountType.Savings);
        var brokerage = b.Account("Brokerage", AccountType.Investment);
        var retirement = b.Category("Retirement");
        b.Txn("2026-08-01", checking, 3_000_00, b.Rta)
            .Assign(retirement, "2026-08", 500_00)
            .Transfer("2026-08-05", checking, brokerage, 500_00, fromCategory: retirement)
            .Transfer("2026-08-06", checking, savings, 1_000_00)                     // on budget ↔ on budget: nothing
            .Transfer("2026-08-07", brokerage, checking, 200_00, toCategory: b.Rta)  // money entering the budget
            .Txn("2026-08-20", brokerage, 75_00, b.Rta);                             // tracking rows never count

        var aug = b.Compute("2026-08", "2026-08").Month(M("2026-08"));
        aug.Category(retirement).Activity.ShouldBe(-500_00);
        aug.Category(retirement).Available.ShouldBe(0);
        aug.InflowThisMonth.ShouldBe(3_200_00);
        aug.ReadyToAssign.ShouldBe(2_700_00);
        aug.UncategorizedActivity.ShouldBe(0);
        aug.TotalActivity.ShouldBe(-500_00);

        return Verify(SnapshotPrinter.Print(b.Compute("2026-08", "2026-08"), b, (retirement, "2026-08")));
    }

    [Fact]
    public Task Month_boundaries_follow_the_civil_calendar_including_leap_day()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var dining = b.Category("Dining");
        b.Txn("2026-08-31", checking, -10_00, dining)
            .Txn("2026-09-01", checking, -20_00, dining)
            .Txn("2026-12-31", checking, -40_00, dining)
            .Txn("2027-01-01", checking, -50_00, dining)
            .Txn("2028-02-28", checking, -1_00, dining)
            .Txn("2028-02-29", checking, -2_00, dining)
            .Txn("2028-03-01", checking, -4_00, dining);

        var snapshot = b.Compute("2026-08", "2028-03");
        snapshot.Months.Count.ShouldBe(20);
        snapshot.Cell(dining, M("2026-08")).Activity.ShouldBe(-10_00);
        snapshot.Cell(dining, M("2026-09")).Activity.ShouldBe(-20_00);
        snapshot.Cell(dining, M("2026-12")).Activity.ShouldBe(-40_00);
        snapshot.Cell(dining, M("2027-01")).Activity.ShouldBe(-50_00);
        snapshot.Cell(dining, M("2028-02")).Activity.ShouldBe(-3_00);
        snapshot.Cell(dining, D("2028-02-29")).Activity.ShouldBe(-3_00); // any day addresses its month
        snapshot.Cell(dining, M("2028-03")).Activity.ShouldBe(-4_00);

        return Verify(SnapshotPrinter.Print(BudgetCalculator.Compute(b.Build(), M("2028-02"), M("2028-03")), b));
    }

    [Fact]
    public Task Closed_accounts_are_included_historically()
    {
        var b = new BudgetBuilder();
        var oldChecking = b.Account("Old Checking", AccountType.Checking, closed: true);
        var oldVisa = b.Account("Old Visa", AccountType.CreditCard, closed: true);
        var groceries = b.Category("Groceries");
        var pay = b.PaymentCategory(oldVisa);
        b.Txn("2026-06-01", oldChecking, 800_00, b.Rta)
            .Assign(groceries, "2026-06", 100_00)
            .Txn("2026-06-10", oldVisa, -100_00, groceries)
            .Transfer("2026-07-05", oldChecking, oldVisa, 100_00);

        var snapshot = b.Compute("2026-06", "2026-08");
        snapshot.Cell(pay, M("2026-06")).Available.ShouldBe(100_00);
        snapshot.Cell(pay, M("2026-07")).Payments.ShouldBe(100_00);
        snapshot.Cell(pay, M("2026-07")).Available.ShouldBe(0);
        snapshot.Month(M("2026-08")).ReadyToAssign.ShouldBe(700_00);

        return Verify(SnapshotPrinter.Print(snapshot, b, (pay, "2026-07")));
    }

    [Fact]
    public void Uncategorized_rows_and_rows_categorized_to_a_payment_category_are_reported_outside_envelopes()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var visa = b.Account("Visa", AccountType.CreditCard);
        b.Txn("2026-08-01", checking, 100_00, b.Rta)
            .Txn("2026-08-02", checking, -12_00, null)
            .Txn("2026-08-03", visa, -8_00, b.PaymentCategory(visa));

        var aug = b.Compute("2026-08", "2026-08").Month(M("2026-08"));
        aug.UncategorizedActivity.ShouldBe(-20_00);
        aug.Category(b.PaymentCategory(visa)).Activity.ShouldBe(0);
        aug.ReadyToAssign.ShouldBe(100_00);
    }

    [Fact]
    public void Assignments_to_ready_to_assign_are_ignored_and_payment_categories_can_be_assigned()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var visa = b.Account("Visa", AccountType.CreditCard);
        b.Txn("2026-08-01", checking, 100_00, b.Rta)
            .Assign(b.Rta, "2026-08", 50_00)
            .Assign(b.PaymentCategory(visa), "2026-08", 30_00);

        var aug = b.Compute("2026-08", "2026-08").Month(M("2026-08"));
        aug.ReadyToAssign.ShouldBe(70_00);
        aug.TotalAssigned.ShouldBe(30_00);
        aug.Category(b.PaymentCategory(visa)).Available.ShouldBe(30_00);
        aug.Categories.ShouldNotContain(c => c.CategoryId == b.Rta);
    }
}
