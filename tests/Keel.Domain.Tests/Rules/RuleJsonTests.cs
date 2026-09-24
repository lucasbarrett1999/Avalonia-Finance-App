using Keel.Domain.Entities;
using Keel.Domain.Rules;
using static Keel.Domain.Tests.Rules.RuleTestData;

namespace Keel.Domain.Tests.Rules;

public class RuleJsonTests
{
    private static RuleDefinition Everything() => Rule(
        "Everything",
        [
            new PayeeCondition(TextOperator.Contains, "trader"),
            new PayeeCondition(TextOperator.EqualTo, "TRADER JOES", Normalized: true),
            new PayeeCondition(TextOperator.StartsWith, "tr"),
            new PayeeCondition(TextOperator.Regex, @"^TRADER\s"),
            new MemoCondition(TextOperator.Contains, "weekly"),
            new AmountCondition(AmountOperator.Between, 1000, 20000),
            new AmountCondition(AmountOperator.EqualTo, -5423, Signed: true),
            new AmountCondition(AmountOperator.GreaterThan, 100),
            new AmountCondition(AmountOperator.LessThan, 100000),
            new DirectionCondition(TransactionDirection.Outflow),
            new AccountCondition([Checking, Visa]),
            new SourceCondition([TransactionSource.File, TransactionSource.Provider]),
            new DateRangeCondition(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)),
            new DateRangeCondition(To: new DateOnly(2026, 12, 31)),
            new TagCondition("business"),
        ],
        [
            new SetPayeeAction("Trader Joe's"),
            new SetCategoryAction(Groceries),
            new SetMemoAction("weekly shop"),
            new AppendMemoAction("#food"),
            new AddTagAction("food"),
            new MarkApprovedAction(),
            new FlagAction(),
            new SplitByAmountsAction([new AmountSplitLine(Household, 1500, "soap"), new AmountSplitLine(Groceries)]),
            new SplitByPercentagesAction([new PercentSplitLine(Groceries, 62.5m), new PercentSplitLine(null, 37.5m, "rest")]),
            new SetTransferAccountAction(Savings),
        ],
        sortOrder: 7,
        continueAfterMatch: true,
        match: RuleMatchMode.Any);

    [Fact]
    public void Every_condition_and_action_round_trips_through_the_entity()
    {
        var rule = Everything();
        var entity = rule.ToEntity();
        var back = RuleDefinition.FromEntity(entity);

        back.Id.ShouldBe(rule.Id);
        back.Name.ShouldBe("Everything");
        back.SortOrder.ShouldBe(7);
        back.ContinueAfterMatch.ShouldBeTrue();
        back.IsEnabled.ShouldBeTrue();
        back.Conditions.Match.ShouldBe(RuleMatchMode.Any);
        back.Conditions.Conditions.Select(c => c.GetType()).ShouldBe(rule.Conditions.Conditions.Select(c => c.GetType()));
        back.Actions.Actions.Select(a => a.GetType()).ShouldBe(rule.Actions.Actions.Select(a => a.GetType()));
        RuleJson.Serialize(back.Conditions).ShouldBe(entity.ConditionsJson);
        RuleJson.Serialize(back.Actions).ShouldBe(entity.ActionsJson);
        back.Describe(Names).ShouldBe(rule.Describe(Names));
    }

    [Fact]
    public void Conditions_json_format_is_stable()
    {
        var json = RuleJson.Serialize(new RuleConditionSet
        {
            Conditions =
            [
                new PayeeCondition(TextOperator.EqualTo, "TRADER JOES", Normalized: true),
                new AmountCondition(AmountOperator.Between, 1000, 20000),
                new DirectionCondition(TransactionDirection.Outflow),
                new SourceCondition([TransactionSource.Provider]),
                new DateRangeCondition(new DateOnly(2026, 1, 1)),
            ],
        });

        json.ShouldBe(
            "{\"version\":1,\"match\":\"all\",\"conditions\":["
            + "{\"type\":\"payee\",\"operator\":\"equals\",\"value\":\"TRADER JOES\",\"normalized\":true},"
            + "{\"type\":\"amount\",\"operator\":\"between\",\"amount\":1000,\"amountMax\":20000,\"signed\":false},"
            + "{\"type\":\"direction\",\"direction\":\"outflow\"},"
            + "{\"type\":\"source\",\"sources\":[\"provider\"]},"
            + "{\"type\":\"dateRange\",\"from\":\"2026-01-01\"}]}");
    }

    [Fact]
    public void Actions_json_format_is_stable()
    {
        var json = RuleJson.Serialize(new RuleActionSet
        {
            Actions =
            [
                new SetCategoryAction(Groceries),
                new MarkApprovedAction(),
                new SplitByAmountsAction([new AmountSplitLine(Household, 1500), new AmountSplitLine(Groceries)]),
                new SplitByPercentagesAction([new PercentSplitLine(null, 50), new PercentSplitLine(Groceries, 50)]),
            ],
        });

        json.ShouldBe(
            "{\"version\":1,\"actions\":["
            + "{\"type\":\"setCategory\",\"categoryId\":\"00000000-0000-7000-8000-00000000c001\"},"
            + "{\"type\":\"markApproved\"},"
            + "{\"type\":\"splitByAmounts\",\"lines\":[{\"categoryId\":\"00000000-0000-7000-8000-00000000c003\",\"amount\":1500},{\"categoryId\":\"00000000-0000-7000-8000-00000000c001\"}]},"
            + "{\"type\":\"splitByPercentages\",\"lines\":[{\"categoryId\":null,\"percent\":50},{\"categoryId\":\"00000000-0000-7000-8000-00000000c001\",\"percent\":50}]}]}");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_documents_read_as_empty_version_1(string? json)
    {
        var conditions = RuleJson.DeserializeConditions(json);
        conditions.Version.ShouldBe(RuleJson.CurrentVersion);
        conditions.Conditions.ShouldBeEmpty();
        conditions.Match.ShouldBe(RuleMatchMode.All);
        RuleJson.DeserializeActions(json).Actions.ShouldBeEmpty();
    }

    [Fact]
    public void The_entity_default_parses_to_an_empty_rule_that_does_not_validate()
    {
        var rule = RuleDefinition.FromEntity(new Rule { Name = "new" });
        rule.Conditions.Conditions.ShouldBeEmpty();
        RuleValidator.IsValid(rule).ShouldBeFalse();
    }

    [Fact]
    public void Discriminator_may_come_after_other_properties()
    {
        var set = RuleJson.DeserializeConditions("{\"conditions\":[{\"value\":\"x\",\"operator\":\"contains\",\"type\":\"memo\"}]}");
        set.Conditions.Single().ShouldBe(new MemoCondition(TextOperator.Contains, "x"));
    }

    [Theory]
    [InlineData("{\"version\":2,\"conditions\":[]}", "newer than this version of Keel understands")]
    [InlineData("{\"conditions\":[{\"type\":\"moonPhase\"}]}", "not valid")]
    [InlineData("{\"conditions\":[{\"type\":\"payee\",\"operator\":\"contains\"}]}", "not valid")]
    [InlineData("{\"conditions\":[{\"type\":\"payee\",\"operator\":\"sounds-like\",\"value\":\"x\"}]}", "not valid")]
    [InlineData("{\"conditions\":[{\"type\":\"direction\",\"direction\":1}]}", "not valid")]
    [InlineData("not json", "not valid")]
    public void Unreadable_or_newer_conditions_throw_a_format_exception(string json, string message)
    {
        Should.Throw<RuleFormatException>(() => RuleJson.DeserializeConditions(json)).Message.ShouldContain(message);
    }

    [Fact]
    public void Newer_actions_throw_a_format_exception()
    {
        Should.Throw<RuleFormatException>(() => RuleJson.DeserializeActions("{\"version\":3}"));
        Should.Throw<RuleFormatException>(() => RuleJson.DeserializeActions("{\"actions\":[{\"type\":\"launchRocket\"}]}"));
    }

    [Fact]
    public void Unknown_properties_are_ignored_for_forward_compatible_additions()
    {
        var set = RuleJson.DeserializeActions("{\"version\":1,\"actions\":[{\"type\":\"flag\",\"color\":\"red\"}],\"extra\":true}");
        set.Actions.Single().ShouldBeOfType<FlagAction>();
    }
}
