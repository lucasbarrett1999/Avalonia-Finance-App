using System.Diagnostics;
using Keel.Domain.Rules;
using static Keel.Domain.Tests.Rules.RuleTestData;

namespace Keel.Domain.Tests.Rules;

public class RuleRegexTests
{
    [Theory]
    [InlineData("([a-z")]
    [InlineData("*abc")]
    [InlineData("a{2,1}")]
    [InlineData(@"\")]
    public void Invalid_patterns_are_rejected_by_the_validator_and_skipped_by_the_engine(string pattern)
    {
        var rule = Rule("bad", [new PayeeCondition(TextOperator.Regex, pattern)], [new SetCategoryAction(Groceries)]);

        var problem = RuleValidator.Validate(rule).ShouldHaveSingleItem();
        problem.Code.ShouldBe(RuleProblemCode.InvalidRegex);
        problem.Severity.ShouldBe(RuleProblemSeverity.Error);
        problem.Path.ShouldBe("conditions[0]");

        var application = RuleEngine.Apply(Txn(), [rule]);
        application.Trace.Evaluations.Single().Outcome.ShouldBe(RuleOutcome.Invalid);
        application.Mutations.HasChanges.ShouldBeFalse();
    }

    [Fact]
    public void Invalid_pattern_message_says_what_is_wrong()
    {
        var rule = Rule("bad", [new MemoCondition(TextOperator.Regex, "([a-z")], [new FlagAction()]);
        RuleValidator.Validate(rule).Single().Message.ShouldBe("Condition 1: the regular expression is not valid (unterminated bracket at position 5).");
    }

    [Fact]
    public void Overlong_patterns_are_rejected()
    {
        var rule = Rule("long", [new PayeeCondition(TextOperator.Regex, new string('a', RuleRegex.MaxPatternLength + 1))], [new FlagAction()]);
        RuleValidator.Validate(rule).Single().Code.ShouldBe(RuleProblemCode.RegexTooLong);
    }

    [Fact]
    public void A_match_that_times_out_counts_as_no_match_and_is_noted()
    {
        // Catastrophic backtracking: (a+)+$ against many a's followed by a non-matching character.
        var payee = new string('a', 40) + "!";
        var rules = new[]
        {
            Rule("slow", [new PayeeCondition(TextOperator.Regex, "^(a+)+$")], [new SetCategoryAction(Dining)], sortOrder: 1),
            Rule("fallback", [new DirectionCondition(TransactionDirection.Outflow)], [new SetCategoryAction(Groceries)], sortOrder: 2),
        };
        var options = new RuleEngineOptions { RegexMatchTimeout = TimeSpan.FromMilliseconds(20) };

        var stopwatch = Stopwatch.StartNew();
        var application = RuleEngine.Apply(Txn(payee), rules, options);
        stopwatch.Stop();

        application.Result.CategoryId.ShouldBe(Groceries);
        var slow = application.Trace.Evaluations[0];
        slow.Outcome.ShouldBe(RuleOutcome.NotMatched);
        slow.Conditions.Single().Note.ShouldBe("regular expression timed out");
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Patterns_are_case_insensitive()
    {
        Matches(new PayeeCondition(TextOperator.Regex, "trader\\s+joe"), Txn()).ShouldBeTrue();
    }
}
