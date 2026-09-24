using Keel.Domain.Rules;
using static Keel.Domain.Tests.Rules.RuleTestData;

namespace Keel.Domain.Tests.Rules;

public class RuleSuggesterTests
{
    [Fact]
    public void Suggests_normalized_payee_direction_and_category()
    {
        var txn = Txn("POS DEBIT TRADER JOE'S #552") with { Payee = "Trader Joe's", CategoryId = Groceries };

        var rule = RuleSuggester.FromTransaction(txn, Names);

        rule.Name.ShouldBe("Trader Joe's → Groceries");
        rule.IsEnabled.ShouldBeTrue();
        rule.ContinueAfterMatch.ShouldBeFalse();
        rule.Conditions.Match.ShouldBe(RuleMatchMode.All);
        rule.Conditions.Conditions.ShouldBe<RuleCondition>(
        [
            new PayeeCondition(TextOperator.EqualTo, "TRADER JOES", Normalized: true),
            new DirectionCondition(TransactionDirection.Outflow),
        ]);
        rule.Actions.Actions.ShouldBe<RuleAction>([new SetPayeeAction("Trader Joe's"), new SetCategoryAction(Groceries)]);
        RuleValidator.Validate(rule).ShouldBeEmpty();
        rule.Describe(Names).ShouldBe("If normalized payee is \"TRADER JOES\" and is an outflow: set payee to \"Trader Joe's\", set category to Groceries");
    }

    [Fact]
    public void Suggested_rule_matches_the_source_and_later_variants_of_the_payee()
    {
        var txn = Txn("SQ *BLUE BOTTLE COFFEE 0423") with { CategoryId = Dining };
        var rule = RuleSuggester.FromTransaction(txn);

        rule.Actions.Actions.ShouldBe<RuleAction>([new SetCategoryAction(Dining)]);
        var uncategorized = txn with { CategoryId = null };
        RuleEngine.Apply(uncategorized, [rule]).Result.CategoryId.ShouldBe(Dining);
        RuleEngine.Apply(Txn("Blue Bottle Coffee #88", amount: -650), [rule]).Result.CategoryId.ShouldBe(Dining);
        RuleEngine.Apply(Txn("Blue Bottle Coffee", amount: 650), [rule]).Mutations.HasChanges.ShouldBeFalse();
    }

    [Fact]
    public void Splits_become_fixed_amounts_with_the_last_line_taking_the_rest()
    {
        var txn = Txn("COSTCO WHSE #1234", amount: -15_000) with
        {
            Splits = [new SnapshotSplit(Groceries, -10_000, "food"), new SnapshotSplit(Household, -3_000), new SnapshotSplit(Gifts, -2_000)],
        };

        var rule = RuleSuggester.FromTransaction(txn);

        var split = rule.Actions.Actions.Single().ShouldBeOfType<SplitByAmountsAction>();
        split.Lines.ShouldBe([new AmountSplitLine(Groceries, 10_000, "food"), new AmountSplitLine(Household, 3_000), new AmountSplitLine(Gifts)]);
        RuleValidator.Validate(rule).ShouldBeEmpty();
        RuleEngine.Apply(txn with { Splits = [] }, [rule]).Result.Splits.ShouldBe(txn.Splits);
    }

    [Fact]
    public void Transfers_and_tags_are_carried_over()
    {
        var txn = Txn("ONLINE TRANSFER TO SAV", amount: -50_000, tags: ["savings"]) with { TransferAccountId = Savings };
        var rule = RuleSuggester.FromTransaction(txn);
        rule.Actions.Actions.ShouldBe<RuleAction>([new SetTransferAccountAction(Savings), new AddTagAction("savings")]);
    }

    [Fact]
    public void An_all_noise_descriptor_falls_back_to_the_raw_payee_and_an_uncategorized_row_has_no_actions()
    {
        var txn = Txn("   ", amount: 1200) with { Payee = "Mom" };
        var rule = RuleSuggester.FromTransaction(txn);

        rule.Name.ShouldBe("Mom");
        rule.Conditions.Conditions.ShouldBe<RuleCondition>([new PayeeCondition(TextOperator.EqualTo, "MOM", Normalized: true), new DirectionCondition(TransactionDirection.Inflow)]);
        rule.Actions.Actions.ShouldBeEmpty();
        RuleValidator.Validate(rule).Single().Code.ShouldBe(RuleProblemCode.NoActions);
    }
}
