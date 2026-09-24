using CsCheck;
using Keel.Domain.Rules;
using static Keel.Domain.Tests.Rules.RuleTestData;

namespace Keel.Domain.Tests.Rules;

public class RuleActionTests
{
    [Fact]
    public void Set_payee_renames_and_keeps_the_raw_descriptor()
    {
        var result = ApplyAction(new SetPayeeAction("  Trader Joe's "), Txn());
        result.Payee.ShouldBe("Trader Joe's");
        result.PayeeRaw.ShouldBe("TRADER JOE'S #552 BROOKLYN NY");
    }

    [Fact]
    public void Set_category_sets_it_and_replaces_existing_splits()
    {
        ApplyAction(new SetCategoryAction(Groceries), Txn()).CategoryId.ShouldBe(Groceries);

        var split = Txn(amount: -1000) with { Splits = [new SnapshotSplit(Dining, -600), new SnapshotSplit(Gifts, -400)] };
        var result = ApplyAction(new SetCategoryAction(Groceries), split);
        result.CategoryId.ShouldBe(Groceries);
        result.Splits.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, "Weekly shop", "Weekly shop")]
    [InlineData("old", "new", "new")]
    [InlineData("old", "", null)]
    public void Set_memo_replaces_or_clears(string? memo, string value, string? expected)
    {
        ApplyAction(new SetMemoAction(value), Txn(memo: memo)).Memo.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "#reimburse", "#reimburse")]
    [InlineData("", "#reimburse", "#reimburse")]
    [InlineData("Lunch  ", "#reimburse", "Lunch #reimburse")]
    [InlineData("Lunch #REIMBURSE", "#reimburse", "Lunch #REIMBURSE")]
    public void Append_memo_is_idempotent(string? memo, string text, string expected)
    {
        ApplyAction(new AppendMemoAction(text), Txn(memo: memo)).Memo.ShouldBe(expected);
    }

    [Fact]
    public void Add_tag_adds_once_ignoring_case()
    {
        ApplyAction(new AddTagAction("business"), Txn(tags: ["trip"])).Tags.ShouldBe(["trip", "business"]);

        var application = RuleEngine.Apply(Txn(tags: ["Business"]), [Rule("tag", [new DirectionCondition(TransactionDirection.Outflow)], [new AddTagAction("business")])]);
        application.Result.Tags.ShouldBe(["Business"]);
        application.Mutations.HasChanges.ShouldBeFalse();
        application.Trace.Evaluations[0].Actions[0].Applied.ShouldBeFalse();
        application.Trace.Evaluations[0].Actions[0].Note.ShouldBe("already tagged \"business\"");
    }

    [Fact]
    public void Mark_approved_and_flag()
    {
        var result = RuleEngine.Apply(Txn(), [Rule("r", [new DirectionCondition(TransactionDirection.Outflow)], [new MarkApprovedAction(), new FlagAction()])]);
        result.Result.IsApproved.ShouldBeTrue();
        result.Result.IsFlagged.ShouldBeTrue();
        result.Mutations.Changes.ShouldBe(RuleChanges.Approved | RuleChanges.Flagged);
    }

    [Fact]
    public void Set_transfer_account()
    {
        ApplyAction(new SetTransferAccountAction(Savings), Txn()).TransferAccountId.ShouldBe(Savings);

        var self = RuleEngine.Apply(Txn(), [Rule("r", [new DirectionCondition(TransactionDirection.Outflow)], [new SetTransferAccountAction(Checking)])]);
        self.Mutations.HasChanges.ShouldBeFalse();
        self.Trace.Evaluations[0].Actions[0].Note.ShouldBe("a transaction cannot be a transfer to its own account");
    }

    [Fact]
    public void Split_by_fixed_amounts_gives_the_rest_to_the_last_line_with_the_parent_sign()
    {
        var action = new SplitByAmountsAction([new AmountSplitLine(Household, 2000, "soap"), new AmountSplitLine(Gifts, 1500), new AmountSplitLine(Groceries)]);
        var result = ApplyAction(action, Txn(amount: -10_000));

        result.CategoryId.ShouldBeNull();
        result.Splits.ShouldBe([new SnapshotSplit(Household, -2000, "soap"), new SnapshotSplit(Gifts, -1500), new SnapshotSplit(Groceries, -6500)]);
        result.Splits.Sum(s => s.Amount).ShouldBe(-10_000);

        var refund = ApplyAction(action, Txn(amount: 4000));
        refund.Splits.Select(s => s.Amount).ShouldBe([2000, 1500, 500]);
    }

    [Theory]
    [InlineData(-3000)]
    [InlineData(-2500)]
    [InlineData(0)]
    public void Split_by_fixed_amounts_is_skipped_when_nothing_is_left(long amount)
    {
        var action = new SplitByAmountsAction([new AmountSplitLine(Household, 2000), new AmountSplitLine(Gifts, 1000), new AmountSplitLine(Groceries)]);
        var application = RuleEngine.Apply(Txn(amount: amount), [Rule("split", [new AmountCondition(AmountOperator.LessThan, long.MaxValue)], [action])]);
        application.Mutations.HasChanges.ShouldBeFalse();
        application.Trace.Evaluations[0].Actions[0].Applied.ShouldBeFalse();
    }

    [Theory]
    [InlineData(-1001, new[] { 50.0, 50.0 }, new long[] { -500, -501 })] // -500.5 rounds to even (-500)
    [InlineData(-1003, new[] { 50.0, 50.0 }, new long[] { -502, -501 })] // -501.5 rounds to even (-502)
    [InlineData(-10_000, new[] { 33.33, 33.33, 33.34 }, new long[] { -3333, -3333, -3334 })]
    [InlineData(-100, new[] { 33.33, 33.33, 33.34 }, new long[] { -33, -33, -34 })]
    [InlineData(1, new[] { 50.0, 50.0 }, new long[] { 0, 1 })] // 0.5 rounds to even (0)
    [InlineData(-2_50, new[] { 12.5, 87.5 }, new long[] { -31, -219 })] // -31.25 rounds to -31
    public void Split_by_percentages_uses_bankers_rounding_with_the_remainder_last(long amount, double[] percents, long[] expected)
    {
        var lines = percents.Select((p, i) => new PercentSplitLine(i % 2 == 0 ? Groceries : Household, (decimal)p)).ToArray();
        var result = ApplyAction(new SplitByPercentagesAction(lines), Txn(amount: amount));
        result.Splits.Select(s => s.Amount).ShouldBe(expected);
        result.Splits.Sum(s => s.Amount).ShouldBe(amount);
    }

    [Fact]
    public void Percentage_splits_always_sum_to_the_parent()
    {
        var gen =
            from amount in Gen.Long[-10_000_000_00, 10_000_000_00].Where(a => a != 0)
            from cuts in Gen.Int[1, 9_999].Array[1, 6]
            select (amount, cuts);

        gen.Sample(t =>
        {
            // Turn random cut points (hundredths of a percent) into percentages that sum to exactly 100.
            var points = t.cuts.Distinct().Order().Select(c => c / 100m).ToList();
            var percents = new List<decimal>();
            var previous = 0m;
            foreach (var point in points)
            {
                percents.Add(point - previous);
                previous = point;
            }

            percents.Add(100m - previous);
            var rule = Rule("p", [new AmountCondition(AmountOperator.LessThan, long.MaxValue)], [new SplitByPercentagesAction(percents.Select(p => new PercentSplitLine(Groceries, p)).ToArray())]);
            RuleValidator.IsValid(rule).ShouldBeTrue();
            var result = RuleEngine.Apply(Txn(amount: t.amount), [rule]).Result;
            result.Splits.Count.ShouldBe(percents.Count);
            result.Splits.Sum(s => s.Amount).ShouldBe(t.amount);
            for (var i = 0; i < percents.Count - 1; i++)
            {
                result.Splits[i].Amount.ShouldBe((long)decimal.Round(t.amount * percents[i] / 100m, 0, MidpointRounding.ToEven));
            }
        });
    }

    [Fact]
    public void Fixed_amount_splits_always_sum_to_the_parent()
    {
        var gen =
            from amount in Gen.Long[-1_000_000_00, 1_000_000_00].Where(a => a != 0)
            from parts in Gen.Long[1, 50_000_00].Array[1, 5]
            select (amount, parts);

        gen.Sample(t =>
        {
            var lines = t.parts.Select(p => new AmountSplitLine(Household, p)).Append(new AmountSplitLine(Groceries)).ToArray();
            var application = RuleEngine.Apply(Txn(amount: t.amount), [Rule("a", [new AmountCondition(AmountOperator.LessThan, long.MaxValue)], [new SplitByAmountsAction(lines)])]);
            if (t.parts.Sum() < Math.Abs(t.amount))
            {
                application.Result.Splits.Sum(s => s.Amount).ShouldBe(t.amount);
                application.Result.Splits.ShouldAllBe(s => Math.Sign(s.Amount) == Math.Sign(t.amount));
            }
            else
            {
                application.Result.IsSplit.ShouldBeFalse();
            }
        });
    }

    [Fact]
    public void Descriptions_read_as_english()
    {
        new SetPayeeAction("Trader Joe's").Describe().ShouldBe("set payee to \"Trader Joe's\"");
        new SetCategoryAction(Groceries).Describe(Names).ShouldBe("set category to Groceries");
        new SetMemoAction(string.Empty).Describe().ShouldBe("clear memo");
        new AppendMemoAction("#r").Describe().ShouldBe("append \"#r\" to memo");
        new AddTagAction("business").Describe().ShouldBe("add tag \"business\"");
        new MarkApprovedAction().Describe().ShouldBe("mark approved");
        new FlagAction().Describe().ShouldBe("flag");
        new SplitByAmountsAction([new AmountSplitLine(Household, 2000), new AmountSplitLine(Groceries)]).Describe(Names)
            .ShouldBe("split into 20.00 to Household, the rest to Groceries");
        new SplitByPercentagesAction([new PercentSplitLine(Household, 62.5m), new PercentSplitLine(null, 37.5m)]).Describe(Names)
            .ShouldBe("split into 62.5% to Household, 37.5% to no category");
        new SetTransferAccountAction(Savings).Describe(Names).ShouldBe("make a transfer with Savings");
    }
}
