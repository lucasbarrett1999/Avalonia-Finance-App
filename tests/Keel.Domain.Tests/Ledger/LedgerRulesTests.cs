using Keel.Domain.Ledger;

namespace Keel.Domain.Tests.Ledger;

public class LedgerRulesTests
{
    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    public void Transfer_category_goes_on_the_on_budget_side_of_an_on_off_budget_transfer(
        bool fromOnBudget, bool toOnBudget, bool fromNeedsCategory, bool toNeedsCategory)
    {
        TransferRules.SideRequiresCategory(fromOnBudget, toOnBudget).ShouldBe(fromNeedsCategory);
        TransferRules.SideRequiresCategory(toOnBudget, fromOnBudget).ShouldBe(toNeedsCategory);
        TransferRules.TransferRequiresCategory(fromOnBudget, toOnBudget).ShouldBe(fromNeedsCategory || toNeedsCategory);
    }

    [Fact]
    public void Transfer_counterpart_is_the_opposite_amount()
    {
        TransferRules.CounterpartAmount(-12_345).ShouldBe(12_345);
        TransferRules.CounterpartAmount(500).ShouldBe(-500);
        Should.Throw<OverflowException>(() => TransferRules.CounterpartAmount(long.MinValue));
    }

    [Fact]
    public void Splits_must_sum_to_the_parent()
    {
        SplitRules.Validate(-10_000, [-6_000, -4_000]).ShouldBeNull();
        SplitRules.Validate(-10_000, [-6_000, -3_000]).ShouldBe(SplitProblem.SumMismatch);
        SplitRules.Remaining(-10_000, [-6_000, -3_000]).ShouldBe(-1_000);
        SplitRules.Validate(-10_000, [-10_000]).ShouldBe(SplitProblem.TooFewLines);
        SplitRules.Validate(-10_000, [-10_000, 0]).ShouldBe(SplitProblem.ZeroLine);
    }

    [Fact]
    public void Splits_may_mix_signs_as_long_as_the_sum_matches()
    {
        // A purchase with a refunded item: -50 groceries, +10 return.
        SplitRules.Validate(-4_000, [-5_000, 1_000]).ShouldBeNull();
    }

    [Fact]
    public void Reconciliation_difference_is_statement_minus_cleared()
    {
        var statementDate = new DateOnly(2026, 8, 31);
        var rows = new[]
        {
            (new DateOnly(2026, 8, 1), 100_000L, TransactionStatus.Reconciled),
            (new DateOnly(2026, 8, 10), -2_500L, TransactionStatus.Cleared),
            (new DateOnly(2026, 8, 20), -7_000L, TransactionStatus.Uncleared),
            (new DateOnly(2026, 9, 2), -1_000L, TransactionStatus.Cleared),
        };

        var cleared = ReconciliationMath.ClearedBalance(rows, statementDate);
        cleared.ShouldBe(97_500);
        ReconciliationMath.Difference(cleared, 97_500).ShouldBe(0);
        ReconciliationMath.Difference(cleared, 97_000).ShouldBe(-500);

        ReconciliationMath.IsLockedByFinish(new DateOnly(2026, 8, 10), TransactionStatus.Cleared, statementDate).ShouldBeTrue();
        ReconciliationMath.IsLockedByFinish(new DateOnly(2026, 8, 20), TransactionStatus.Uncleared, statementDate).ShouldBeFalse();
        ReconciliationMath.IsLockedByFinish(new DateOnly(2026, 9, 2), TransactionStatus.Cleared, statementDate).ShouldBeFalse();
    }

    [Fact]
    public void Running_balance_follows_ledger_order_not_input_order()
    {
        var t0 = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var early = Guid.CreateVersion7(t0);
        var sameDayFirst = Guid.CreateVersion7(t0.AddSeconds(1));
        var sameDaySecond = Guid.CreateVersion7(t0.AddSeconds(2));
        var rows = new[]
        {
            (sameDaySecond, new DateOnly(2026, 8, 5), -300L),
            (early, new DateOnly(2026, 8, 1), 1_000L),
            (sameDayFirst, new DateOnly(2026, 8, 5), -200L),
        };

        var balances = RunningBalance.Compute(rows, openingBalance: 50);

        balances[early].ShouldBe(1_050);
        balances[sameDayFirst].ShouldBe(850);
        balances[sameDaySecond].ShouldBe(550);
        RunningBalance.CompareLedgerOrder(new DateOnly(2026, 8, 5), sameDayFirst, new DateOnly(2026, 8, 5), sameDaySecond).ShouldBeLessThan(0);
    }

    [Theory]
    [InlineData("  Trader   Joe's ", "Trader Joe's", "TRADER JOE'S")]
    [InlineData("", "", "")]
    [InlineData(null, "", "")]
    public void Payee_names_are_cleaned_and_normalized(string? input, string clean, string normalized)
    {
        PayeeNames.Clean(input).ShouldBe(clean);
        PayeeNames.Normalize(input).ShouldBe(normalized);
    }
}
