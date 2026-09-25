using CsCheck;
using Keel.Domain.Debt;

namespace Keel.Domain.Tests.Debt;

public sealed class DebtPayoffCalculatorTests
{
    private static readonly Guid IdA = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid IdB = new("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid IdC = new("00000000-0000-0000-0000-00000000000c");

    private static DebtInput Debt(Guid id, string name, long balance, int rateBps, long minimum) => new(id, name, balance, rateBps, minimum);

    [Theory]
    [InlineData(1_000_00, 1200, 10_00)]   // $1,000 at 12%: $10.00
    [InlineData(125, 1200, 1)]            // 1.25 -> 1
    [InlineData(150, 1200, 2)]            // 1.5 -> 2 (half to even)
    [InlineData(250, 1200, 2)]            // 2.5 -> 2 (half to even)
    [InlineData(350, 1200, 4)]            // 3.5 -> 4
    [InlineData(819_10, 1200, 8_19)]      // 8.191 -> 8.19
    [InlineData(1_000_00, 0, 0)]          // zero rate
    [InlineData(0, 1999, 0)]              // nothing owed
    [InlineData(-500_00, 1999, 0)]        // a credit balance earns nothing here
    [InlineData(long.MaxValue / 2, 10_000, 384_307_168_202_282_325)] // no overflow (Int128)
    public void Monthly_interest_is_rate_over_twelve_with_bankers_rounding(long balance, int bps, long expected) =>
        DebtPayoffCalculator.MonthlyInterest(balance, bps).ShouldBe(expected);

    [Fact]
    public void Amortizes_a_single_debt_month_by_month()
    {
        // $1,000 at 12% with $100 a month; interest by hand: 10.00, 9.10, 8.19, 7.27, 6.35, 5.41, 4.46, 3.51, 2.54, 1.57, 0.58.
        var schedule = DebtPayoffCalculator.AtMinimum(Debt(IdA, "Card", 1_000_00, 1200, 100_00));

        schedule.PayoffMonths.ShouldBe(11);
        schedule.TotalInterest.ShouldBe(58_98);
        schedule.TotalPaid.ShouldBe(1_058_98);
        schedule.FirstPayment.ShouldBe(100_00);
        schedule.Balances.ShouldBe([1_000_00, 910_00, 819_10, 727_29, 634_56, 540_91, 446_32, 350_78, 254_29, 156_83, 58_40, 0]);
    }

    [Fact]
    public void Zero_rate_pays_down_linearly_without_interest()
    {
        var schedule = DebtPayoffCalculator.AtMinimum(Debt(IdA, "Family loan", 1_000_00, 0, 100_00));
        schedule.PayoffMonths.ShouldBe(10);
        schedule.TotalInterest.ShouldBe(0);
        schedule.Balances.ShouldBe(Enumerable.Range(0, 11).Select(i => 1_000_00L - (i * 100_00L)).ToList());
    }

    [Fact]
    public void One_month_payoff_pays_the_balance_plus_that_months_interest()
    {
        var schedule = DebtPayoffCalculator.AtMinimum(Debt(IdA, "Store card", 50_00, 1200, 100_00));
        schedule.PayoffMonths.ShouldBe(1);
        schedule.TotalInterest.ShouldBe(50);
        schedule.FirstPayment.ShouldBe(50_50);
        schedule.Balances.ShouldBe([50_00, 0]);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-120_00L)]
    public void A_debt_already_at_zero_is_paid_off_at_month_zero(long balance)
    {
        var schedule = DebtPayoffCalculator.AtMinimum(Debt(IdA, "Paid", balance, 1999, 25_00));
        schedule.PayoffMonths.ShouldBe(0);
        schedule.StartingBalance.ShouldBe(0);
        schedule.TotalInterest.ShouldBe(0);
        schedule.TotalPaid.ShouldBe(0);
        schedule.FirstPayment.ShouldBe(0);
        schedule.Balances.ShouldBe([0]);

        var plan = DebtPayoffCalculator.Plan([Debt(IdA, "Paid", balance, 1999, 25_00)], 50_00, DebtOrdering.Snowball);
        plan.PayoffMonths.ShouldBe(0);
        plan.MonthlyOutlay.ShouldBe(50_00); // a paid-off debt's minimum is not part of the outlay
        plan.TotalBalances.ShouldBe([0]);
    }

    [Fact]
    public void A_payment_below_the_interest_never_pays_off()
    {
        // $10,000 at 24% charges $200 a month; $150 never catches up, so the plan stops at once.
        var schedule = DebtPayoffCalculator.AtMinimum(Debt(IdA, "Loan", 10_000_00, 2400, 150_00));
        schedule.PaysOff.ShouldBeFalse();
        schedule.PayoffMonths.ShouldBeNull();
        schedule.Balances.ShouldBe([10_000_00]);

        var plan = DebtPayoffCalculator.Plan([Debt(IdA, "Loan", 10_000_00, 2400, 150_00)], 0, DebtOrdering.Avalanche);
        plan.PaysOffAll.ShouldBeFalse();
        plan.PayoffMonths.ShouldBeNull();

        // Enough extra fixes it.
        DebtPayoffCalculator.Plan([Debt(IdA, "Loan", 10_000_00, 2400, 150_00)], 150_00, DebtOrdering.Avalanche).PaysOffAll.ShouldBeTrue();
    }

    [Fact]
    public void A_payment_equal_to_the_interest_never_pays_off()
    {
        var schedule = DebtPayoffCalculator.AtMinimum(Debt(IdA, "Interest only", 10_000_00, 1200, 100_00));
        schedule.PayoffMonths.ShouldBeNull();
        schedule.Balances.ShouldBe([10_000_00]);
        schedule.TotalInterest.ShouldBe(0);
    }

    [Fact]
    public void Other_debts_still_pay_off_next_to_one_that_never_does()
    {
        // The card's $200 interest exceeds the whole $150 outlay, so it never shrinks; the loan still pays off in 10 months.
        var plan = DebtPayoffCalculator.Plan([Debt(IdA, "Card", 10_000_00, 2400, 50_00), Debt(IdB, "Loan", 1_000_00, 0, 100_00)], 0, DebtOrdering.Avalanche);
        plan.Debts.Select(d => d.Name).ShouldBe(["Card", "Loan"]);
        plan.Debts[0].PayoffMonths.ShouldBeNull();
        plan.Debts[1].PayoffMonths.ShouldBe(10);
        plan.PayoffMonths.ShouldBeNull();
        plan.TotalBalances.Count.ShouldBe(11); // stops once only the hopeless debt is left
        plan.Debts[0].Balances[^1].ShouldBeGreaterThan(10_000_00);
    }

    [Fact]
    public void A_debt_that_outgrows_the_outlay_later_is_never_paid_off_and_balances_stay_bounded()
    {
        // At 100% a year the card grows until its interest passes the $1,100 outlay; the loan still finishes.
        var plan = DebtPayoffCalculator.Plan([Debt(IdA, "Card", 10_000_00, 10_000, 800_00), Debt(IdB, "Loan", 30_000_00, 0, 300_00)], 0, DebtOrdering.Snowball);
        plan.Debts.Single(d => d.Name == "Card").PayoffMonths.ShouldBeNull();
        plan.Debts.Single(d => d.Name == "Loan").PayoffMonths.ShouldBe(100);
        plan.Debts.ShouldAllBe(d => d.Balances.All(b => b >= 0 && b <= DebtPayoffCalculator.BalanceCeiling));
        plan.TotalBalances.Count.ShouldBeLessThanOrEqualTo(DebtPayoffCalculator.MaxMonths + 1);
    }

    [Fact]
    public void Zero_minimum_and_zero_rate_never_pays_off_without_extra()
    {
        DebtPayoffCalculator.AtMinimum(Debt(IdA, "Parked", 500_00, 0, 0)).PayoffMonths.ShouldBeNull();
        DebtPayoffCalculator.Plan([Debt(IdA, "Parked", 500_00, 0, 0)], 100_00, DebtOrdering.Snowball).PayoffMonths.ShouldBe(5);
    }

    [Fact]
    public void Snowball_rolls_the_extra_and_freed_minimums_into_the_next_debt()
    {
        // A: $100 min $30; B: $300 min $10; extra $20, no interest. Outlay $60 a month.
        // m1 A 100-30-20 = 50, B 290; m2 A 50-30-20 = 0, B 280; then B gets all 60: 220, 160, 100, 40, 0.
        var plan = DebtPayoffCalculator.Plan([Debt(IdB, "B", 300_00, 0, 10_00), Debt(IdA, "A", 100_00, 0, 30_00)], 20_00, DebtOrdering.Snowball);

        plan.MonthlyOutlay.ShouldBe(60_00);
        plan.Debts.Select(d => d.Name).ShouldBe(["A", "B"]);
        plan.Debts[0].PayoffMonths.ShouldBe(2);
        plan.Debts[0].FirstPayment.ShouldBe(50_00);
        plan.Debts[1].PayoffMonths.ShouldBe(7);
        plan.Debts[1].FirstPayment.ShouldBe(10_00);
        plan.Debts[1].Balances.ShouldBe([300_00, 290_00, 280_00, 220_00, 160_00, 100_00, 40_00, 0]);
        plan.Debts[0].Balances.ShouldBe([100_00, 50_00, 0, 0, 0, 0, 0, 0]);
        plan.TotalBalances.ShouldBe([400_00, 340_00, 280_00, 220_00, 160_00, 100_00, 40_00, 0]);
        plan.PayoffMonths.ShouldBe(7);
        plan.TotalInterest.ShouldBe(0);
    }

    [Fact]
    public void Money_left_over_in_a_payoff_month_goes_to_the_next_debt_the_same_month()
    {
        // A owes $20 with a $30 minimum: the $10 left over goes to B in month 1.
        var plan = DebtPayoffCalculator.Plan([Debt(IdA, "A", 20_00, 0, 30_00), Debt(IdB, "B", 100_00, 0, 10_00)], 0, DebtOrdering.Snowball);
        plan.Debts[0].PayoffMonths.ShouldBe(1);
        plan.Debts[0].FirstPayment.ShouldBe(20_00);
        plan.Debts[1].FirstPayment.ShouldBe(20_00);
        plan.Debts[1].Balances.ShouldBe([100_00, 80_00, 40_00, 0]);
        plan.PayoffMonths.ShouldBe(3);
    }

    [Fact]
    public void Snowball_and_avalanche_order_debts_with_tie_breakers()
    {
        var debts = new[]
        {
            Debt(IdA, "Visa", 2_000_00, 2499, 60_00),
            Debt(IdB, "Car", 8_000_00, 649, 250_00),
            Debt(IdC, "Store", 400_00, 2499, 25_00),
        };
        DebtPayoffCalculator.Order(debts, DebtOrdering.Snowball).Select(d => d.Name).ShouldBe(["Store", "Visa", "Car"]);
        DebtPayoffCalculator.Order(debts, DebtOrdering.Avalanche).Select(d => d.Name).ShouldBe(["Store", "Visa", "Car"]); // same rate: smaller first

        var tie = new[] { Debt(IdB, "Zeta", 500_00, 1000, 10_00), Debt(IdA, "Alpha", 500_00, 1000, 10_00) };
        DebtPayoffCalculator.Order(tie, DebtOrdering.Snowball).Select(d => d.Name).ShouldBe(["Alpha", "Zeta"]);
        DebtPayoffCalculator.Order([Debt(IdA, "Low", 100_00, 500, 10_00), Debt(IdB, "High", 900_00, 2000, 10_00)], DebtOrdering.Avalanche)
            .Select(d => d.Name).ShouldBe(["High", "Low"]);
    }

    [Fact]
    public void Avalanche_never_costs_more_interest_than_snowball_here_and_both_beat_minimums()
    {
        var debts = new[]
        {
            Debt(IdA, "Visa", 4_500_00, 2299, 90_00),
            Debt(IdB, "Car", 9_000_00, 549, 280_00),
            Debt(IdC, "Store", 700_00, 2699, 35_00),
        };
        var snowball = DebtPayoffCalculator.Plan(debts, 200_00, DebtOrdering.Snowball);
        var avalanche = DebtPayoffCalculator.Plan(debts, 200_00, DebtOrdering.Avalanche);
        var minimum = DebtPayoffCalculator.MinimumOnly(debts);

        snowball.PaysOffAll.ShouldBeTrue();
        avalanche.PaysOffAll.ShouldBeTrue();
        minimum.PaysOffAll.ShouldBeTrue();
        avalanche.TotalInterest.ShouldBeLessThanOrEqualTo(snowball.TotalInterest);
        snowball.TotalInterest.ShouldBeLessThan(minimum.TotalInterest);
        snowball.PayoffMonths!.Value.ShouldBeLessThan(minimum.PayoffMonths!.Value);
        minimum.MonthlyOutlay.ShouldBe(405_00);
        snowball.MonthlyOutlay.ShouldBe(605_00);

        // Until the final month every month pays exactly the outlay.
        foreach (var plan in new[] { snowball, avalanche })
        {
            var paid = plan.Debts.Sum(d => d.TotalPaid);
            paid.ShouldBe(plan.Debts.Sum(d => d.StartingBalance) + plan.TotalInterest);
            paid.ShouldBeGreaterThan(plan.MonthlyOutlay * (plan.PayoffMonths!.Value - 1));
            paid.ShouldBeLessThanOrEqualTo(plan.MonthlyOutlay * plan.PayoffMonths.Value);
        }
    }

    [Fact]
    public void Minimum_only_keeps_each_debt_on_its_own_without_rollover()
    {
        var plan = DebtPayoffCalculator.MinimumOnly([Debt(IdA, "A", 100_00, 0, 50_00), Debt(IdB, "B", 300_00, 0, 100_00)]);
        plan.Debts.Select(d => d.PayoffMonths).ShouldBe([2, 3]);
        plan.PayoffMonths.ShouldBe(3);
        plan.TotalBalances.ShouldBe([400_00, 250_00, 100_00, 0]);
        plan.Debts[0].Balances.ShouldBe([100_00, 50_00, 0, 0]);

        var never = DebtPayoffCalculator.MinimumOnly([Debt(IdA, "A", 100_00, 0, 50_00), Debt(IdB, "B", 10_000_00, 1200, 100_00)]);
        never.PayoffMonths.ShouldBeNull();
    }

    [Fact]
    public void Rejects_negative_extra_negative_minimums_and_out_of_range_rates()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => DebtPayoffCalculator.Plan([Debt(IdA, "A", 1_00, 0, 1_00)], -1, DebtOrdering.Snowball));
        Should.Throw<ArgumentOutOfRangeException>(() => DebtPayoffCalculator.Plan([Debt(IdA, "A", 1_00, 0, -1)], 0, DebtOrdering.Snowball));
        Should.Throw<ArgumentOutOfRangeException>(() => DebtPayoffCalculator.Plan([Debt(IdA, "A", 1_00, -1, 1_00)], 0, DebtOrdering.Snowball));
        Should.Throw<ArgumentOutOfRangeException>(() => DebtPayoffCalculator.Plan([Debt(IdA, "A", 1_00, 10_001, 1_00)], 0, DebtOrdering.Snowball));
    }

    [Fact]
    public void Plans_with_no_debts_are_empty()
    {
        var plan = DebtPayoffCalculator.Plan([], 100_00, DebtOrdering.Avalanche);
        plan.Debts.ShouldBeEmpty();
        plan.PayoffMonths.ShouldBe(0);
        plan.TotalBalances.ShouldBe([0]);
    }

    [Fact]
    public void Matches_a_naive_decimal_reference_and_conserves_money()
    {
        var gen = Gen.Select(Gen.Long[0, 5_000_000], Gen.Int[0, 3000], Gen.Long[1_000, 400_000]);
        gen.Sample((balance, bps, minimum) =>
        {
            var schedule = DebtPayoffCalculator.AtMinimum(Debt(IdA, "X", balance, bps, minimum));
            var reference = Reference(balance, bps, minimum, schedule.Balances.Count - 1);
            schedule.Balances.ShouldBe(reference);
            schedule.TotalPaid.ShouldBe(balance + schedule.TotalInterest - schedule.Balances[^1]);
            if (schedule.PaysOff)
            {
                schedule.Balances[^1].ShouldBe(0);
            }
        }, iter: 300);
    }

    // Independent reference: decimal arithmetic, Math.Round half to even, one debt, fixed payment.
    private static List<long> Reference(long balance, int bps, long payment, int months)
    {
        var list = new List<long> { balance };
        decimal owed = balance;
        for (var m = 0; m < months; m++)
        {
            owed += Math.Round(owed * bps / 120_000m, MidpointRounding.ToEven);
            owed -= Math.Min(owed, payment);
            list.Add((long)owed);
        }

        return list;
    }
}
