using Keel.Domain.Rules;
using static Keel.Domain.Tests.Rules.RuleTestData;

namespace Keel.Domain.Tests.Rules;

public class RuleConditionTests
{
    [Theory]
    [InlineData(TextOperator.Contains, "joe", true)]
    [InlineData(TextOperator.Contains, "JOE'S #552", true)]
    [InlineData(TextOperator.Contains, "whole foods", false)]
    [InlineData(TextOperator.EqualTo, "trader joe's #552 brooklyn ny", true)]
    [InlineData(TextOperator.EqualTo, "  TRADER JOE'S #552 BROOKLYN NY  ", true)]
    [InlineData(TextOperator.EqualTo, "TRADER JOE'S", false)]
    [InlineData(TextOperator.StartsWith, "trader", true)]
    [InlineData(TextOperator.StartsWith, "joe", false)]
    [InlineData(TextOperator.Regex, @"^TRADER\s+JOE", true)]
    [InlineData(TextOperator.Regex, @"#\d{3}\b", true)]
    [InlineData(TextOperator.Regex, @"^JOE", false)]
    public void Payee_text_operators_ignore_case(TextOperator op, string value, bool expected)
    {
        Matches(new PayeeCondition(op, value), Txn()).ShouldBe(expected);
    }

    [Theory]
    [InlineData(TextOperator.EqualTo, "Trader Joe's", true)]
    [InlineData(TextOperator.EqualTo, "TRADER JOES", true)]
    [InlineData(TextOperator.EqualTo, "TRADER JOE'S #552", true)]
    [InlineData(TextOperator.Contains, "joes", true)]
    [InlineData(TextOperator.StartsWith, "trader jo", true)]
    [InlineData(TextOperator.Regex, "^TRADER JOES$", true)]
    [InlineData(TextOperator.Regex, "'", false)]
    [InlineData(TextOperator.EqualTo, "TRADER", false)]
    public void Normalized_payee_variants_compare_normalized_text(TextOperator op, string value, bool expected)
    {
        Matches(new PayeeCondition(op, value, Normalized: true), Txn("  POS DEBIT TRADER JOE'S #552 ")).ShouldBe(expected);
    }

    [Fact]
    public void Payee_conditions_match_the_renamed_payee_or_the_raw_descriptor()
    {
        var renamed = Txn("AMZN Mktp US*2K4AB1CD2") with { Payee = "Amazon" };
        Matches(new PayeeCondition(TextOperator.EqualTo, "amazon"), renamed).ShouldBeTrue();
        Matches(new PayeeCondition(TextOperator.StartsWith, "AMZN MKTP"), renamed).ShouldBeTrue();
        Matches(new PayeeCondition(TextOperator.Contains, "costco"), renamed).ShouldBeFalse();
    }

    [Theory]
    [InlineData(TextOperator.Contains, "refund", "Partial REFUND for order", true)]
    [InlineData(TextOperator.Contains, "refund", null, false)]
    [InlineData(TextOperator.EqualTo, "gift", "Gift", true)]
    [InlineData(TextOperator.StartsWith, "inv", "INV-2026-001", true)]
    [InlineData(TextOperator.Regex, @"INV-\d{4}-\d+", "ref INV-2026-001", true)]
    [InlineData(TextOperator.Regex, @"^$", null, true)]
    public void Memo_conditions(TextOperator op, string value, string? memo, bool expected)
    {
        Matches(new MemoCondition(op, value), Txn(memo: memo)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(AmountOperator.EqualTo, 5423, null, false, -5423, true)]
    [InlineData(AmountOperator.EqualTo, 5423, null, false, 5423, true)]
    [InlineData(AmountOperator.EqualTo, -5423, null, true, -5423, true)]
    [InlineData(AmountOperator.EqualTo, 5423, null, true, -5423, false)]
    [InlineData(AmountOperator.Between, 5000, 6000, false, -5423, true)]
    [InlineData(AmountOperator.Between, 5423, 5423, false, -5423, true)]
    [InlineData(AmountOperator.Between, 5424, 6000, false, -5423, false)]
    [InlineData(AmountOperator.Between, -6000, -5000, true, -5423, true)]
    [InlineData(AmountOperator.GreaterThan, 5000, null, false, -5423, true)]
    [InlineData(AmountOperator.GreaterThan, 5423, null, false, -5423, false)]
    [InlineData(AmountOperator.GreaterThan, 0, null, true, -5423, false)]
    [InlineData(AmountOperator.LessThan, 10000, null, false, -5423, true)]
    [InlineData(AmountOperator.LessThan, 5423, null, false, -5423, false)]
    [InlineData(AmountOperator.LessThan, 0, null, true, -5423, true)]
    public void Amount_conditions(AmountOperator op, long amount, int? max, bool signed, long txnAmount, bool expected)
    {
        Matches(new AmountCondition(op, amount, max, signed), Txn(amount: txnAmount)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(TransactionDirection.Outflow, -1, true)]
    [InlineData(TransactionDirection.Outflow, 1, false)]
    [InlineData(TransactionDirection.Inflow, 1, true)]
    [InlineData(TransactionDirection.Inflow, -1, false)]
    [InlineData(TransactionDirection.Inflow, 0, false)]
    [InlineData(TransactionDirection.Outflow, 0, false)]
    public void Direction_conditions(TransactionDirection direction, long amount, bool expected)
    {
        Matches(new DirectionCondition(direction), Txn(amount: amount)).ShouldBe(expected);
    }

    [Fact]
    public void Account_condition_is_membership()
    {
        var condition = new AccountCondition([Visa, Savings]);
        Matches(condition, Txn(account: Visa)).ShouldBeTrue();
        Matches(condition, Txn(account: Checking)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(TransactionSource.File, true)]
    [InlineData(TransactionSource.Provider, true)]
    [InlineData(TransactionSource.Manual, false)]
    public void Source_condition_is_membership(TransactionSource source, bool expected)
    {
        Matches(new SourceCondition([TransactionSource.File, TransactionSource.Provider]), Txn(source: source)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("2026-09-01", "2026-09-30", "2026-09-14", true)]
    [InlineData("2026-09-14", "2026-09-14", "2026-09-14", true)]
    [InlineData("2026-09-15", null, "2026-09-14", false)]
    [InlineData(null, "2026-09-13", "2026-09-14", false)]
    [InlineData(null, "2026-09-14", "2026-09-14", true)]
    [InlineData("2026-01-01", null, "2026-09-14", true)]
    public void Date_range_is_inclusive_and_may_be_open(string? from, string? to, string date, bool expected)
    {
        var condition = new DateRangeCondition(from is null ? null : DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), to is null ? null : DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture));
        Matches(condition, Txn(date: DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture))).ShouldBe(expected);
    }

    [Fact]
    public void Tag_condition_ignores_case()
    {
        Matches(new TagCondition("Business"), Txn(tags: ["business", "trip"])).ShouldBeTrue();
        Matches(new TagCondition("reimbursable"), Txn(tags: ["business"])).ShouldBeFalse();
        Matches(new TagCondition("business"), Txn()).ShouldBeFalse();
    }

    [Fact]
    public void All_requires_every_condition_and_any_requires_one()
    {
        RuleCondition[] conditions = [new PayeeCondition(TextOperator.Contains, "trader"), new AmountCondition(AmountOperator.GreaterThan, 100_00)];
        var all = RuleEngine.Apply(Txn(), [Rule("all", conditions, [new FlagAction()])]);
        var any = RuleEngine.Apply(Txn(), [Rule("any", conditions, [new FlagAction()], match: RuleMatchMode.Any)]);

        all.Trace.Evaluations[0].Outcome.ShouldBe(RuleOutcome.NotMatched);
        all.Trace.Evaluations[0].Conditions.Select(c => c.Matched).ShouldBe([true, false]);
        any.Trace.Evaluations[0].Outcome.ShouldBe(RuleOutcome.Matched);
        any.Result.IsFlagged.ShouldBeTrue();
    }

    [Fact]
    public void Descriptions_read_as_english()
    {
        new PayeeCondition(TextOperator.Contains, "trader").Describe().ShouldBe("payee contains \"trader\"");
        new PayeeCondition(TextOperator.EqualTo, "TRADER JOES", true).Describe().ShouldBe("normalized payee is \"TRADER JOES\"");
        new MemoCondition(TextOperator.Regex, "^x").Describe().ShouldBe("memo matches /^x/");
        new AmountCondition(AmountOperator.Between, 1000, 5000).Describe().ShouldBe("amount is between 10.00 and 50.00");
        new AmountCondition(AmountOperator.LessThan, 0, Signed: true).Describe().ShouldBe("signed amount is less than 0.00");
        new DirectionCondition(TransactionDirection.Outflow).Describe().ShouldBe("is an outflow");
        new AccountCondition([Checking, Visa]).Describe(Names).ShouldBe("account is Checking or Visa");
        new SourceCondition([TransactionSource.File, TransactionSource.Provider, TransactionSource.Manual]).Describe().ShouldBe("source is file import, bank sync or manual entry");
        new DateRangeCondition(new DateOnly(2026, 1, 1), null).Describe().ShouldBe("date is on or after 2026-01-01");
        new TagCondition("business").Describe().ShouldBe("has tag \"business\"");
    }
}
