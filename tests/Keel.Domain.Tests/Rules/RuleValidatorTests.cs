using Keel.Domain.Rules;
using static Keel.Domain.Tests.Rules.RuleTestData;

namespace Keel.Domain.Tests.Rules;

public class RuleValidatorTests
{
    private static readonly RuleCondition Ok = new DirectionCondition(TransactionDirection.Outflow);
    private static readonly RuleAction Act = new FlagAction();

    private static RuleProblem Single(RuleCondition condition) =>
        RuleValidator.Validate(Rule("r", [condition], [Act])).ShouldHaveSingleItem();

    private static RuleProblem Single(RuleAction action, RuleValidationContext? context = null) =>
        RuleValidator.Validate(Rule("r", [Ok], [action]), context).ShouldHaveSingleItem();

    [Fact]
    public void A_complete_rule_has_no_problems()
    {
        RuleValidator.Validate(Rule("Groceries", [new PayeeCondition(TextOperator.Contains, "trader")], [new SetCategoryAction(Groceries)])).ShouldBeEmpty();
    }

    [Fact]
    public void Empty_rule_reports_name_conditions_and_actions()
    {
        var problems = RuleValidator.Validate(new RuleDefinition());
        problems.Select(p => (p.Code, p.Path, p.Message)).ShouldBe(
        [
            (RuleProblemCode.NameMissing, "name", "Give the rule a name."),
            (RuleProblemCode.NoConditions, "conditions", "Add at least one condition; a rule without conditions would match every transaction."),
            (RuleProblemCode.NoActions, "actions", "Add at least one action; this rule would not change anything."),
        ]);
    }

    [Fact]
    public void Condition_messages()
    {
        Single(new PayeeCondition(TextOperator.Contains, " ")).Message.ShouldBe("Condition 1: enter the payee text to compare with.");
        Single(new MemoCondition(TextOperator.EqualTo, "")).Message.ShouldBe("Condition 1: enter the memo text to compare with.");
        Single(new AmountCondition(AmountOperator.GreaterThan, -5)).Message
            .ShouldBe("Condition 1: amounts compare the size of the transaction, so enter a positive number (use a direction condition for inflow or outflow).");
        Single(new AmountCondition(AmountOperator.Between, 5)).Message.ShouldBe("Condition 1: enter the upper amount of the range.");
        Single(new AmountCondition(AmountOperator.Between, 50, 10)).Message.ShouldBe("Condition 1: the lower amount is greater than the upper amount.");
        Single(new AccountCondition([])).Message.ShouldBe("Condition 1: choose at least one account.");
        Single(new SourceCondition([])).Message.ShouldBe("Condition 1: choose at least one source.");
        Single(new DateRangeCondition()).Message.ShouldBe("Condition 1: set a start date, an end date, or both.");
        Single(new DateRangeCondition(new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1))).Message
            .ShouldBe("Condition 1: the end date 2026-01-01 is before the start date 2026-02-01.");
        Single(new TagCondition("")).Message.ShouldBe("Condition 1: enter a tag name.");
    }

    [Fact]
    public void Signed_amounts_may_be_negative()
    {
        RuleValidator.Validate(Rule("r", [new AmountCondition(AmountOperator.Between, -500, -100, Signed: true)], [Act])).ShouldBeEmpty();
    }

    [Fact]
    public void An_unused_upper_bound_is_a_warning()
    {
        var problem = Single(new AmountCondition(AmountOperator.GreaterThan, 5, AmountMax: 10));
        problem.Severity.ShouldBe(RuleProblemSeverity.Warning);
        problem.Message.ShouldBe("Condition 1: the upper amount is ignored unless the comparison is \"between\".");
        RuleValidator.IsValid(Rule("r", [new AmountCondition(AmountOperator.GreaterThan, 5, AmountMax: 10)], [Act])).ShouldBeTrue();
    }

    [Fact]
    public void Action_messages()
    {
        Single(new SetPayeeAction(" ")).Message.ShouldBe("Action 1: enter the new payee name.");
        Single(new SetCategoryAction(Guid.Empty)).Message.ShouldBe("Action 1: choose a category.");
        Single(new AppendMemoAction("")).Message.ShouldBe("Action 1: enter the text to append to the memo.");
        Single(new AddTagAction("")).Message.ShouldBe("Action 1: enter a tag name.");
        Single(new SetTransferAccountAction(Guid.Empty)).Message.ShouldBe("Action 1: choose an account.");
        Single(new SplitByAmountsAction([new AmountSplitLine(Groceries)])).Message.ShouldBe("Action 1: a split needs at least two lines.");
        Single(new SplitByAmountsAction([new AmountSplitLine(Groceries, 0), new AmountSplitLine(Household)])).Message
            .ShouldBe("Action 1, line 1: enter an amount greater than zero.");
        Single(new SplitByAmountsAction([new AmountSplitLine(Groceries, 10), new AmountSplitLine(Household, 5)])).Message
            .ShouldBe("Action 1, line 2: the last line always receives the rest of the amount; leave its amount empty.");
        Single(new SplitByPercentagesAction([new PercentSplitLine(Groceries, 60), new PercentSplitLine(Household, 30)])).Message
            .ShouldBe("Action 1: the percentages add up to 90%; they must add up to 100%.");
        var problems = RuleValidator.Validate(Rule("r", [Ok], [new SplitByPercentagesAction([new PercentSplitLine(Groceries, 0), new PercentSplitLine(Household, 100)])]));
        problems.Single().Message.ShouldBe("Action 1, line 1: enter a percentage greater than zero.");
    }

    [Fact]
    public void Setting_the_same_field_twice_is_a_warning()
    {
        var problems = RuleValidator.Validate(Rule("r", [Ok],
        [
            new SetCategoryAction(Groceries),
            new SetPayeeAction("A"),
            new SplitByPercentagesAction([new PercentSplitLine(Groceries, 50), new PercentSplitLine(Household, 50)]),
        ]));

        var problem = problems.ShouldHaveSingleItem();
        problem.Severity.ShouldBe(RuleProblemSeverity.Warning);
        problem.Path.ShouldBe("actions[2]");
        problem.Message.ShouldBe("Action 3 sets the category or split again; it replaces what action 1 did.");
    }

    [Fact]
    public void References_are_checked_against_a_context()
    {
        var context = new RuleValidationContext(new HashSet<Guid> { Groceries }, new HashSet<Guid> { Checking });
        Single(new SetCategoryAction(Dining), context).Message.ShouldBe("Action 1: the category no longer exists; choose another one.");
        Single(new SetTransferAccountAction(Savings), context).Message.ShouldBe("Action 1: the account no longer exists; choose another one.");
        Single(new SplitByAmountsAction([new AmountSplitLine(Dining, 5), new AmountSplitLine(Groceries)]), context).Path.ShouldBe("actions[0].lines[0]");
        RuleValidator.Validate(Rule("r", [new AccountCondition([Visa])], [new SetCategoryAction(Groceries)]), context).Single().Code.ShouldBe(RuleProblemCode.UnknownAccount);
        RuleValidator.Validate(Rule("r", [new AccountCondition([Checking])], [new SetCategoryAction(Groceries)]), context).ShouldBeEmpty();
    }
}
