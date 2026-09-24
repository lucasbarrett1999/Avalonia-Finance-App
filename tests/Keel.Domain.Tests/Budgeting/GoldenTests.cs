using Keel.Domain.Budgeting;
using static VerifyXunit.Verifier;

namespace Keel.Domain.Tests.Budgeting;

/// <summary>
/// PRD 6.4.7 and 6.4.8, implemented exactly as written. The numbers asserted here are the spec;
/// the Verify golden files record every intermediate value for review.
/// </summary>
public class GoldenTests
{
    // 6.4.7 ------------------------------------------------------------------------------------

    private static (BudgetBuilder B, Guid Checking, Guid Visa, Guid Groceries, Guid Rent, Guid PayVisa) Example647()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var visa = b.Account("Visa", AccountType.CreditCard);
        var groceries = b.Category("Groceries");
        var rent = b.Category("Rent");
        var payVisa = b.PaymentCategory(visa);

        b.Txn("2026-08-01", checking, 3_000_00, b.Rta)     // paycheck
            .Assign(rent, "2026-08", 1_500_00)
            .Assign(groceries, "2026-08", 400_00)
            .Txn("2026-08-05", checking, -1_500_00, rent)
            .Txn("2026-08-10", visa, -250_00, groceries)
            .Txn("2026-08-20", visa, -200_00, groceries)
            .Transfer("2026-08-25", checking, visa, 100_00); // payment
        return (b, checking, visa, groceries, rent, payVisa);
    }

    [Fact]
    public Task Golden_1_worked_example_6_4_7()
    {
        var (b, _, visa, groceries, rent, payVisa) = Example647();
        var snapshot = b.Compute("2026-08", "2026-09");

        var aug = snapshot.Month(BudgetBuilder.M("2026-08"));
        var g = aug.Category(groceries);
        g.Activity.ShouldBe(-450_00);
        g.RawAvailable.ShouldBe(-50_00);                  // 0 + 400 − 450
        g.Available.ShouldBe(-50_00);
        g.CreditSpending.ShouldBe(450_00);
        g.CreditOverspent.ShouldBe(-50_00);
        g.CashOverspent.ShouldBe(0);
        g.Overspending.ShouldBe(BudgetOverspending.Credit); // yellow

        g.CoveredByCard.ShouldBe([new AccountAmount(visa, 400_00)]);
        var pay = aug.Category(payVisa);
        pay.Covered.ShouldBe(400_00);
        pay.Payments.ShouldBe(100_00);
        pay.Activity.ShouldBe(300_00);                    // 400 − 100
        pay.Available.ShouldBe(300_00);                   // 0 + 0 + 300
        pay.Overspending.ShouldBe(BudgetOverspending.None);

        aug.Category(rent).Available.ShouldBe(0);
        aug.ReadyToAssign.ShouldBe(1_100_00);             // 3,000 − 1,900

        var sep = snapshot.Month(BudgetBuilder.M("2026-09"));
        sep.Category(groceries).Carry.ShouldBe(0);        // max(0, −50)
        sep.ReadyToAssign.ShouldBe(1_100_00);             // credit overspending does not reduce RTA
        sep.Category(payVisa).Carry.ShouldBe(300_00);

        return Verify(SnapshotPrinter.Print(snapshot, b, (groceries, "2026-08"), (payVisa, "2026-08"), (payVisa, "2026-09")));
    }

    [Fact]
    public Task Golden_1_worked_example_6_4_7_after_assigning_50_more_to_groceries()
    {
        var (b, _, visa, groceries, _, payVisa) = Example647();
        b.Assign(groceries, "2026-08", 50_00);           // after the fact: Groceries assigned 450 in total
        var snapshot = b.Compute("2026-08", "2026-09");

        var aug = snapshot.Month(BudgetBuilder.M("2026-08"));
        aug.Category(groceries).Assigned.ShouldBe(450_00);
        aug.Category(groceries).Available.ShouldBe(0);
        aug.Category(groceries).Overspending.ShouldBe(BudgetOverspending.None);
        aug.Category(groceries).CoveredByCard.ShouldBe([new AccountAmount(visa, 450_00)]);
        aug.Category(payVisa).Covered.ShouldBe(450_00);
        aug.Category(payVisa).Available.ShouldBe(350_00);
        aug.ReadyToAssign.ShouldBe(1_050_00);

        return Verify(SnapshotPrinter.Print(snapshot, b, (groceries, "2026-08"), (payVisa, "2026-08")));
    }

    // 6.4.8 ------------------------------------------------------------------------------------

    private static (BudgetBuilder B, Guid Dining) Example648(long spent)
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var dining = b.Category("Dining");
        b.Assign(dining, "2026-08", 100_00)
            .Txn("2026-08-15", checking, -spent, dining);
        return (b, dining);
    }

    [Fact]
    public Task Golden_2_cash_overspending_6_4_8()
    {
        var (b, dining) = Example648(spent: 130_00);
        var snapshot = b.Compute("2026-08", "2026-09");

        var aug = snapshot.Month(BudgetBuilder.M("2026-08"));
        aug.Category(dining).Available.ShouldBe(-30_00);
        aug.Category(dining).CashOverspent.ShouldBe(-30_00);
        aug.Category(dining).CreditOverspent.ShouldBe(0);
        aug.Category(dining).Overspending.ShouldBe(BudgetOverspending.Cash); // red

        var sep = snapshot.Month(BudgetBuilder.M("2026-09"));
        sep.Category(dining).Carry.ShouldBe(0);

        // RTA(09) is reduced by 30 relative to 6.4.1 without the overspending.
        var withoutOverspendingTerm = sep.InflowThroughMonth - sep.AssignedThroughMonth - sep.AssignedInFuture;
        sep.ReadyToAssign.ShouldBe(withoutOverspendingTerm - 30_00);
        var (counterfactual, _) = Example648(spent: 100_00);
        var baseline = counterfactual.Compute("2026-08", "2026-09").Month(BudgetBuilder.M("2026-09")).ReadyToAssign;
        sep.ReadyToAssign.ShouldBe(baseline - 30_00);

        // Overspending reduces the NEXT month's RTA, not the month itself.
        aug.ReadyToAssign.ShouldBe(-100_00);

        return Verify(SnapshotPrinter.Print(snapshot, b, (dining, "2026-08"), (dining, "2026-09")));
    }
}
