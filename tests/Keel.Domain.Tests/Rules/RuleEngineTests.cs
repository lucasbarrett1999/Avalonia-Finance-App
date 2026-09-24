using Keel.Domain.Rules;
using static Keel.Domain.Tests.Rules.RuleTestData;

namespace Keel.Domain.Tests.Rules;

public class RuleEngineTests
{
    private static readonly RuleCondition IsTraderJoes = new PayeeCondition(TextOperator.Contains, "trader joe");
    private static readonly RuleCondition IsOutflow = new DirectionCondition(TransactionDirection.Outflow);

    [Fact]
    public void First_match_wins_and_later_rules_are_not_evaluated()
    {
        var rules = new[]
        {
            Rule("groceries", [IsTraderJoes], [new SetCategoryAction(Groceries)], sortOrder: 1),
            Rule("dining", [IsOutflow], [new SetCategoryAction(Dining)], sortOrder: 2),
        };

        var application = RuleEngine.Apply(Txn(), rules);

        application.Result.CategoryId.ShouldBe(Groceries);
        application.Trace.Evaluations.Count.ShouldBe(1);
        application.Trace.StoppedByRuleId.ShouldBe(IdFor("groceries"));
        application.Mutations.SetBy[RuleChanges.Category].ShouldBe(IdFor("groceries"));
    }

    [Fact]
    public void Rules_run_by_sort_order_not_input_order()
    {
        var rules = new[]
        {
            Rule("dining", [IsOutflow], [new SetCategoryAction(Dining)], sortOrder: 2),
            Rule("groceries", [IsTraderJoes], [new SetCategoryAction(Groceries)], sortOrder: 1),
        };

        RuleEngine.Apply(Txn(), rules).Result.CategoryId.ShouldBe(Groceries);
    }

    [Fact]
    public void Equal_sort_orders_keep_input_order()
    {
        var a = Rule("a", [IsOutflow], [new SetCategoryAction(Dining)]);
        var b = Rule("b", [IsOutflow], [new SetCategoryAction(Groceries)]);
        RuleEngine.Apply(Txn(), [a, b]).Result.CategoryId.ShouldBe(Dining);
        RuleEngine.Apply(Txn(), [b, a]).Result.CategoryId.ShouldBe(Groceries);
    }

    [Fact]
    public void Continue_after_match_lets_later_rules_run_and_see_earlier_changes()
    {
        var rules = new[]
        {
            Rule("rename", [new PayeeCondition(TextOperator.StartsWith, "AMZN")], [new SetPayeeAction("Amazon")], sortOrder: 1, continueAfterMatch: true),
            Rule("tag big", [new AmountCondition(AmountOperator.GreaterThan, 100_00)], [new AddTagAction("big")], sortOrder: 2, continueAfterMatch: true),
            Rule("amazon", [new PayeeCondition(TextOperator.EqualTo, "Amazon")], [new SetCategoryAction(Household)], sortOrder: 3),
            Rule("never reached", [IsOutflow], [new FlagAction()], sortOrder: 4),
        };

        var application = RuleEngine.Apply(Txn("AMZN Mktp US*2K4AB1CD2", amount: -2599), rules);

        application.Result.Payee.ShouldBe("Amazon");
        application.Result.CategoryId.ShouldBe(Household);
        application.Result.Tags.ShouldBeEmpty();
        application.Result.IsFlagged.ShouldBeFalse();
        application.Trace.Evaluations.Select(e => (e.RuleName, e.Outcome)).ShouldBe(
        [
            ("rename", RuleOutcome.Matched),
            ("tag big", RuleOutcome.NotMatched),
            ("amazon", RuleOutcome.Matched),
        ]);
        application.Trace.Matched.Select(e => e.RuleName).ShouldBe(["rename", "amazon"]);
        application.Mutations.Changes.ShouldBe(RuleChanges.Payee | RuleChanges.Category);
        application.Mutations.SetBy[RuleChanges.Payee].ShouldBe(IdFor("rename"));
        application.Mutations.SetBy[RuleChanges.Category].ShouldBe(IdFor("amazon"));
    }

    [Fact]
    public void A_later_continuing_rule_overrides_an_earlier_one_for_the_same_field()
    {
        var rules = new[]
        {
            Rule("broad", [IsOutflow], [new SetCategoryAction(Dining)], sortOrder: 1, continueAfterMatch: true),
            Rule("specific", [IsTraderJoes], [new SetCategoryAction(Groceries)], sortOrder: 2, continueAfterMatch: true),
        };

        var application = RuleEngine.Apply(Txn(), rules);
        application.Result.CategoryId.ShouldBe(Groceries);
        application.Trace.StoppedByRuleId.ShouldBeNull();
        application.Mutations.SetBy[RuleChanges.Category].ShouldBe(IdFor("specific"));
    }

    [Fact]
    public void Disabled_and_invalid_rules_are_skipped_and_traced()
    {
        var rules = new[]
        {
            Rule("off", [IsOutflow], [new SetCategoryAction(Dining)], sortOrder: 1, enabled: false),
            Rule("broken", [new PayeeCondition(TextOperator.Regex, "([a-z")], [new SetCategoryAction(Dining)], sortOrder: 2),
            Rule("groceries", [IsTraderJoes], [new SetCategoryAction(Groceries)], sortOrder: 3),
        };

        var application = RuleEngine.Apply(Txn(), rules);

        application.Result.CategoryId.ShouldBe(Groceries);
        application.Trace.Evaluations.Select(e => e.Outcome).ShouldBe([RuleOutcome.Disabled, RuleOutcome.Invalid, RuleOutcome.Matched]);
        application.Trace.Evaluations[1].Problems.Single().ShouldStartWith("Condition 1: the regular expression is not valid (");
    }

    [Fact]
    public void No_match_leaves_the_transaction_unchanged()
    {
        var input = Txn();
        var application = RuleEngine.Apply(input, [Rule("x", [new PayeeCondition(TextOperator.Contains, "costco")], [new SetCategoryAction(Groceries)])]);
        application.Mutations.HasChanges.ShouldBeFalse();
        application.Mutations.DecidesCategory.ShouldBeFalse();
        application.Result.ShouldBe(input);
        application.Trace.Matched.ShouldBeEmpty();
    }

    [Fact]
    public void No_rules_yield_an_empty_trace()
    {
        var application = RuleEngine.Apply(Txn(), []);
        application.Trace.Evaluations.ShouldBeEmpty();
        application.Trace.Describe().ShouldBe("No rules.");
    }

    [Fact]
    public void Splitting_and_transfers_count_as_deciding_the_category()
    {
        var split = RuleEngine.Apply(Txn(amount: -1000), [Rule("s", [IsOutflow], [new SplitByPercentagesAction([new PercentSplitLine(Groceries, 50), new PercentSplitLine(Household, 50)])])]);
        split.Mutations.DecidesCategory.ShouldBeTrue();
        split.Mutations.Changes.ShouldBe(RuleChanges.Splits);

        var transfer = RuleEngine.Apply(Txn(), [Rule("t", [IsOutflow], [new SetTransferAccountAction(Savings)])]);
        transfer.Mutations.DecidesCategory.ShouldBeTrue();

        var memoOnly = RuleEngine.Apply(Txn(), [Rule("m", [IsOutflow], [new SetMemoAction("x")])]);
        memoOnly.Mutations.DecidesCategory.ShouldBeFalse();
    }

    [Fact]
    public void Split_then_set_category_in_the_same_rule_keeps_the_last()
    {
        var application = RuleEngine.Apply(Txn(amount: -1000), [Rule("r", [IsOutflow],
        [
            new SplitByPercentagesAction([new PercentSplitLine(Groceries, 50), new PercentSplitLine(Household, 50)]),
            new SetCategoryAction(Dining),
        ])]);

        application.Result.CategoryId.ShouldBe(Dining);
        application.Result.IsSplit.ShouldBeFalse();
        application.Mutations.Changes.ShouldBe(RuleChanges.Category);
    }

    [Fact]
    public void Engine_is_deterministic_and_does_not_mutate_its_inputs()
    {
        var input = Txn(tags: ["a"]);
        var rules = new[]
        {
            Rule("r1", [IsOutflow], [new AddTagAction("b"), new AppendMemoAction("x")], continueAfterMatch: true),
            Rule("r2", [IsTraderJoes], [new SetCategoryAction(Groceries)], sortOrder: 1),
        };
        var compiled = RuleEngine.Compile(rules);

        var first = compiled.Apply(input);
        var second = compiled.Apply(input);

        input.Tags.ShouldBe(["a"]);
        input.Memo.ShouldBeNull();
        second.Result.ShouldBe(first.Result);
        second.Trace.Describe().ShouldBe(first.Trace.Describe());
        first.Mutations.AddedTags.ShouldBe(["b"]);
    }

    [Fact]
    public void Apply_all_supports_retroactive_preview()
    {
        var compiled = RuleEngine.Compile([Rule("g", [IsTraderJoes], [new SetCategoryAction(Groceries)])]);
        var results = compiled.ApplyAll([Txn(), Txn("COSTCO WHSE #123"), Txn("Trader Joe's")]);
        results.Select(r => r.Mutations.HasChanges).ShouldBe([true, false, true]);
    }

    [Fact]
    public void Trace_describes_the_run()
    {
        var rules = new[]
        {
            Rule("Rename Amazon", [new PayeeCondition(TextOperator.StartsWith, "AMZN")], [new SetPayeeAction("Amazon")], sortOrder: 1, continueAfterMatch: true),
            Rule("Big", [new AmountCondition(AmountOperator.GreaterThan, 100_00)], [new FlagAction()], sortOrder: 2),
            Rule("Amazon", [new PayeeCondition(TextOperator.EqualTo, "amazon"), IsOutflow], [new SetCategoryAction(Household), new AddTagAction("online")], sortOrder: 3),
        };

        var trace = RuleEngine.Apply(Txn("AMZN Mktp US*2K4AB1CD2", amount: -2599), rules, new RuleEngineOptions { Names = Names }).Trace.Describe();

        trace.ShouldBe(
            "'Rename Amazon': matched [payee starts with \"AMZN\" = yes] -> set payee to \"Amazon\"\n"
            + "'Big': did not match [amount is more than 100.00 = no]\n"
            + "'Amazon': matched [payee is \"amazon\" = yes; is an outflow = yes] -> set category to Household; add tag \"online\"; stopped");
    }
}
