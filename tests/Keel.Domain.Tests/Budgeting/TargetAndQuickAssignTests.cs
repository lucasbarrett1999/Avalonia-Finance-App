using Keel.Domain.Budgeting;
using Keel.Domain.Entities;
using static Keel.Domain.Tests.Budgeting.BudgetBuilder;
using Target = Keel.Domain.Entities.Target;

namespace Keel.Domain.Tests.Budgeting;

public class TargetAndQuickAssignTests
{
    private static CategoryMonthResult Cell(long carry = 0, long assigned = 0, long activity = 0, string month = "2026-08") => new()
    {
        CategoryId = Guid.NewGuid(),
        Month = M(month),
        Kind = BudgetCategoryKind.Regular,
        IsVisible = true,
        PreviousAvailable = carry,
        Carry = carry,
        Assigned = assigned,
        Activity = activity,
        RawAvailable = carry + assigned + activity,
        Available = carry + assigned + activity,
        CashOverspent = 0,
        CreditOverspent = 0,
    };

    private static Target T(TargetType type, long amount, string? date = null) =>
        new() { CategoryId = Guid.NewGuid(), Type = type, Amount = amount, TargetDate = date is null ? null : D(date) };

    [Theory]
    [InlineData(0, 100_00)]
    [InlineData(40_00, 60_00)]
    [InlineData(100_00, 0)]
    [InlineData(150_00, 0)]
    public void Monthly_set_aside_needs_the_amount_assigned_every_month(long assigned, long underfunded)
    {
        var status = TargetCalculator.Compute(T(TargetType.MonthlySetAside, 100_00), Cell(carry: 500_00, assigned: assigned));
        status.NeededThisMonth.ShouldBe(100_00);
        status.Underfunded.ShouldBe(underfunded);
        status.MonthlyNeed.ShouldBe(100_00);
        status.IsFunded.ShouldBe(underfunded == 0);
    }

    [Fact]
    public void Debt_payment_needs_the_amount_every_month()
    {
        var status = TargetCalculator.Compute(T(TargetType.DebtPayment, 250_00), Cell(assigned: 100_00));
        status.Underfunded.ShouldBe(150_00);
        status.MonthlyNeed.ShouldBe(250_00);
    }

    [Theory]
    [InlineData(0, 0, -80_00, 300_00)]       // spending this month does not raise the need
    [InlineData(120_00, 0, 0, 180_00)]       // refill: what was carried in counts
    [InlineData(120_00, 180_00, -300_00, 0)]
    [InlineData(400_00, 0, 0, 0)]            // already above the refill level
    public void Monthly_spending_refills_to_the_amount(long carry, long assigned, long activity, long underfunded)
    {
        var status = TargetCalculator.Compute(T(TargetType.MonthlySpending, 300_00), Cell(carry, assigned, activity));
        status.Underfunded.ShouldBe(underfunded);
        status.NeededThisMonth.ShouldBe(Math.Max(0, 300_00 - carry));
    }

    [Fact]
    public void Savings_by_date_spreads_the_missing_balance_over_the_remaining_months_rounding_up()
    {
        // 1,000.00 by December starting in October with nothing saved: 3 months → 333.34 now.
        var status = TargetCalculator.Compute(T(TargetType.SavingsBalanceByDate, 1_000_00, "2026-12-15"), Cell(month: "2026-10"));
        status.MonthsRemaining.ShouldBe(3);
        status.MonthlyNeed.ShouldBe(333_34);
        status.Underfunded.ShouldBe(333_34);
        status.IsComplete.ShouldBeFalse();

        // November with 333.34 carried: 666.66 over 2 months → 333.33.
        TargetCalculator.Compute(T(TargetType.SavingsBalanceByDate, 1_000_00, "2026-12-15"), Cell(carry: 333_34, month: "2026-11"))
            .MonthlyNeed.ShouldBe(333_33);

        // December: the rest; assigned this month counts toward it.
        var december = TargetCalculator.Compute(T(TargetType.SavingsBalanceByDate, 1_000_00, "2026-12-15"), Cell(carry: 666_67, assigned: 300_00, month: "2026-12"));
        december.MonthlyNeed.ShouldBe(333_33);
        december.Underfunded.ShouldBe(33_33);

        // Spending from the balance this month raises the need.
        TargetCalculator.Compute(T(TargetType.SavingsBalanceByDate, 1_000_00, "2026-12-01"), Cell(carry: 900_00, activity: -100_00, month: "2026-12"))
            .Underfunded.ShouldBe(200_00);
    }

    [Fact]
    public void Savings_by_date_past_the_date_or_complete()
    {
        var overdue = TargetCalculator.Compute(T(TargetType.SavingsBalanceByDate, 500_00, "2026-01-31"), Cell(carry: 200_00, month: "2026-08"));
        overdue.MonthsRemaining.ShouldBe(1);
        overdue.Underfunded.ShouldBe(300_00);

        var complete = TargetCalculator.Compute(T(TargetType.SavingsBalanceByDate, 500_00, "2027-01-31"), Cell(carry: 600_00, month: "2026-08"));
        complete.IsComplete.ShouldBeTrue();
        complete.Underfunded.ShouldBe(0);

        TargetCalculator.Compute(T(TargetType.SavingsBalanceByDate, 500_00), Cell(month: "2026-08")).Underfunded.ShouldBe(500_00);
    }

    [Fact]
    public void Unknown_target_type_throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => TargetCalculator.Compute(T((TargetType)99, 1), Cell()));

    [Theory]
    [InlineData(5, 2, 2)]
    [InlineData(7, 2, 4)]
    [InlineData(-5, 2, -2)]
    [InlineData(-7, 2, -4)]
    [InlineData(10, 3, 3)]
    [InlineData(11, 3, 4)]
    [InlineData(-11, 3, -4)]
    [InlineData(0, 3, 0)]
    public void Divide_half_even(long numerator, long denominator, long expected) =>
        QuickAssign.DivideHalfEven(numerator, denominator).ShouldBe(expected);

    [Fact]
    public void Divide_half_even_rejects_non_positive_denominators() =>
        Should.Throw<ArgumentOutOfRangeException>(() => QuickAssign.DivideHalfEven(1, 0));

    [Fact]
    public void Quick_assign_values_come_from_the_previous_months()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var groceries = b.Category("Groceries");
        b.Assign(groceries, "2026-05", 400_00).Txn("2026-05-10", checking, -390_00, groceries)
            .Assign(groceries, "2026-06", 350_00).Txn("2026-06-10", checking, -410_00, groceries)
            .Txn("2026-06-20", checking, 20_00, groceries)
            .Assign(groceries, "2026-07", 300_01).Txn("2026-07-10", checking, -310_00, groceries)
            .Assign(groceries, "2026-08", 50_00);
        var snapshot = b.Compute("2026-05", "2026-08");
        var target = new TargetStatus(TargetType.MonthlySetAside, 420_00, null, 420_00, 370_00, 420_00, null, false);

        var values = QuickAssign.Compute(snapshot, groceries, M("2026-08"), target);
        values.AssignedLastMonth.ShouldBe(300_01);
        values.SpentLastMonth.ShouldBe(310_00);
        values.AverageAssigned.ShouldBe(350_00);          // 1,050.01 / 3 = 350.0033…
        values.AverageSpent.ShouldBe(363_33);             // 1,090.00 / 3 = 363.333…
        values.FundTarget.ShouldBe(420_00);               // 50 assigned + 370 underfunded
        values.ResetToZero.ShouldBe(0);

        QuickAssign.Compute(snapshot, groceries, M("2026-08"), null).FundTarget.ShouldBeNull();
    }

    [Fact]
    public void Spent_values_are_floored_at_zero_when_refunds_dominate()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var gifts = b.Category("Gifts");
        b.Txn("2026-07-10", checking, 25_00, gifts);
        var values = QuickAssign.Compute(b.Compute("2026-04", "2026-08"), gifts, M("2026-08"), null);
        values.SpentLastMonth.ShouldBe(0);
        values.AverageSpent.ShouldBe(0);
    }
}
