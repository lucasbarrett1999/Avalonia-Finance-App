using Keel.Application.Categorization;
using Keel.Domain.Categorization;
using Keel.Domain.Rules;
using static Keel.Domain.Tests.Categorization.LearnerTestData;

namespace Keel.Domain.Tests.Categorization;

public class CategorizationEngineTests
{
    private static readonly ICategorizationEngine Engine = new CategorizationEngine();

    private static readonly LearnerModel Model = CategoryLearner.Train(
        [
            .. Repeat(12, "TRADER JOE'S #552", Groceries),
            Ex("TRADER JOE'S", Household, -1299),
            .. Repeat(10, "TARGET", Household, -2500),
            .. Repeat(4, "AMAZON", Household, -2000),
            .. Repeat(3, "AMAZON", Electronics, -2000),
        ],
        categories: Catalog);

    private static RuleDefinition Rule(string name, RuleCondition condition, params RuleAction[] actions) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Conditions = new RuleConditionSet { Conditions = [condition] },
        Actions = new RuleActionSet { Actions = actions },
    };

    [Fact]
    public void A_rule_that_sets_a_category_decides_and_the_learner_is_skipped()
    {
        var rules = new[] { Rule("Trader Joe's is dining", new PayeeCondition(TextOperator.Contains, "trader"), new SetCategoryAction(Dining)) };

        var result = Engine.Categorize(Txn("TRADER JOE'S #9"), rules, Model, new PayeeDefaultCategory(Household, "Household"));

        result.DecidedBy.ShouldBe(CategorizationSource.Rule);
        result.Result.CategoryId.ShouldBe(Dining);
        result.Trace.Learner.ShouldBeNull();
        result.Suggestions.ShouldBe([new CategorizationSuggestion(Dining, "Dining Out", 1.0, CategorizationSource.Rule, "Set by rule 'Trader Joe's is dining'", true)]);
        result.Trace.Steps.Select(s => (s.Stage, s.Outcome)).ShouldBe(
        [
            (CategorizationStage.Rules, CategorizationStepOutcome.Decided),
            (CategorizationStage.PayeeDefault, CategorizationStepOutcome.Skipped),
            (CategorizationStage.Learner, CategorizationStepOutcome.Skipped),
        ]);
        result.Trace.Summary.ShouldBe("Category set to Dining Out by rule 'Trader Joe's is dining'.");
    }

    [Fact]
    public void A_rule_that_splits_or_makes_a_transfer_also_decides()
    {
        var split = Rule("Costco split", new PayeeCondition(TextOperator.Contains, "costco"), new SplitByPercentagesAction([new PercentSplitLine(Groceries, 70), new PercentSplitLine(Household, 30)]));
        var result = Engine.Categorize(Txn("COSTCO WHSE", -10_000), [split], Model, null);
        result.DecidedBy.ShouldBe(CategorizationSource.Rule);
        result.CategoryId.ShouldBeNull();
        result.Result.Splits.Sum(s => s.Amount).ShouldBe(-10_000);
        result.Suggestions.ShouldBeEmpty();
        result.Trace.Summary.ShouldBe("Split by rule 'Costco split' into 2 parts.");

        var transfer = Rule("To savings", new PayeeCondition(TextOperator.Contains, "transfer"), new SetTransferAccountAction(Checking));
        var moved = Engine.Categorize(Txn("ONLINE TRANSFER"), [transfer], Model, null);
        moved.DecidedBy.ShouldBe(CategorizationSource.Rule);
        moved.Trace.Summary.ShouldBe("Made a transfer by rule 'To savings'.");
    }

    [Fact]
    public void Rule_changes_other_than_category_are_kept_and_the_next_stage_decides()
    {
        var rename = Rule("Rename", new PayeeCondition(TextOperator.StartsWith, "POS DEBIT TRADER"), new SetPayeeAction("Trader Joe's"), new AddTagAction("food"));

        var result = Engine.Categorize(Txn("POS DEBIT TRADER JOE'S #1"), [rename], Model, null);

        result.DecidedBy.ShouldBe(CategorizationSource.Learner);
        result.Result.Payee.ShouldBe("Trader Joe's");
        result.Result.Tags.ShouldBe(["food"]);
        result.Result.CategoryId.ShouldBe(Groceries);
        result.Trace.Steps[0].Detail.ShouldBe("Matched 'Rename'; none set a category.");
    }

    [Fact]
    public void The_payee_default_is_suggested_at_95_percent_with_learner_alternatives()
    {
        var result = Engine.Categorize(Txn("AMAZON", -2000), [], Model, new PayeeDefaultCategory(Electronics, "Electronics"));

        result.DecidedBy.ShouldBe(CategorizationSource.PayeeDefault);
        result.CategoryId.ShouldBe(Electronics);
        result.Result.CategoryId.ShouldBe(Electronics);
        var first = result.Suggestions[0];
        first.ShouldBe(new CategorizationSuggestion(Electronics, "Electronics", 0.95, CategorizationSource.PayeeDefault, "Suggested because AMAZON's default category is Electronics", true));
        result.Suggestions.Skip(1).ShouldAllBe(s => s.Source == CategorizationSource.Learner && !s.IsPrimary && s.CategoryId != Electronics);
        result.Trace.Summary.ShouldBe("Suggested because AMAZON's default category is Electronics (95%).");
        result.Trace.Steps.Select(s => s.Stage).ShouldBe([CategorizationStage.Rules, CategorizationStage.PayeeDefault, CategorizationStage.Learner]);
    }

    [Fact]
    public void Without_rules_or_payee_default_the_learner_decides_and_explains()
    {
        var result = Engine.Categorize(Txn("POS DEBIT TRADER JOE'S #558"), [], Model, null);

        result.DecidedBy.ShouldBe(CategorizationSource.Learner);
        result.CategoryId.ShouldBe(Groceries);
        result.Suggestions[0].Explanation.ShouldBe("Suggested because 12 of 13 past 'TRADER JOES' transactions were Groceries");
        result.Suggestions[0].IsPrimary.ShouldBeTrue();
        result.Trace.Learner!.Outcome.ShouldBe(LearnerOutcome.Suggested);
        result.Trace.Summary.ShouldStartWith("Suggested because 12 of 13 past 'TRADER JOES' transactions were Groceries (");
    }

    [Fact]
    public void Low_confidence_or_no_history_leaves_the_transaction_uncategorized()
    {
        var unknown = Engine.Categorize(Txn("ZANZIBAR IMPORTS"), [], Model, null);
        unknown.DecidedBy.ShouldBe(CategorizationSource.None);
        unknown.CategoryId.ShouldBeNull();
        unknown.Suggestions.ShouldBeEmpty();
        unknown.Trace.Summary.ShouldBe("Left uncategorized. Not suggested: no approved transactions from 'ZANZIBAR IMPORTS' or with its words yet.");

        var noModel = Engine.Categorize(Txn("TRADER JOE'S"), [], null, null);
        noModel.DecidedBy.ShouldBe(CategorizationSource.None);
        noModel.Trace.Summary.ShouldBe("Left uncategorized: no rule, payee default or learning history applies.");
    }

    [Fact]
    public void An_already_categorized_transaction_is_kept()
    {
        var result = Engine.Categorize(Txn("TRADER JOE'S") with { CategoryId = Dining }, [], Model, new PayeeDefaultCategory(Groceries, "Groceries"));
        result.DecidedBy.ShouldBe(CategorizationSource.Existing);
        result.Result.CategoryId.ShouldBe(Dining);
        result.Trace.Steps[0].Stage.ShouldBe(CategorizationStage.Input);
        result.Trace.Learner.ShouldBeNull();
    }

    [Fact]
    public void Top_n_caps_the_suggestions()
    {
        Engine.Categorize(Txn("AMAZON", -2000), [], Model, new PayeeDefaultCategory(Electronics, "Electronics"), topN: 1).Suggestions.Count.ShouldBe(1);
        Engine.Categorize(Txn("AMAZON", -2000), [], Model, null, topN: 0).Suggestions.ShouldBeEmpty();
    }

    [Fact]
    public void Compiled_and_uncompiled_rules_give_the_same_result()
    {
        var rules = new[] { Rule("r", new PayeeCondition(TextOperator.Contains, "target"), new SetCategoryAction(Household), new FlagAction()) };
        var txn = Txn("TARGET");
        var a = Engine.Categorize(txn, rules, Model, null);
        var b = Engine.Categorize(txn, RuleEngine.Compile(rules), Model, null);
        b.Result.ShouldBe(a.Result);
        b.Suggestions.ShouldBe(a.Suggestions);
        b.Trace.Summary.ShouldBe(a.Trace.Summary);
    }
}
