using Keel.Domain.Categorization;
using static Keel.Domain.Tests.Categorization.LearnerTestData;

namespace Keel.Domain.Tests.Categorization;

public class LearnerModelDeterminismTests
{
    private static readonly IReadOnlyList<LabeledExample> History = LabeledHistoryGenerator.Generate(months: 6, seed: 11);

    private static LearnerModel Train(IEnumerable<LabeledExample> examples) =>
        CategoryLearner.Train(examples, categories: LabeledHistoryGenerator.Catalog);

    private static string Predictions(LearnerModel model) => string.Join('\n', History.Take(150).Select(e =>
    {
        var p = model.Predict(e.Transaction, 3);
        return $"{p.Outcome}|{p.Reason}|" + string.Join(";", p.Suggestions.Select(s => $"{s.CategoryId}:{s.Confidence:R}:{s.Explanation}"));
    }));

    [Fact]
    public void Same_input_gives_the_same_model_json_and_predictions()
    {
        var first = Train(History);
        var second = Train(History.ToList());

        second.ToJson().ShouldBe(first.ToJson());
        Predictions(second).ShouldBe(Predictions(first));
    }

    [Fact]
    public void Example_order_does_not_change_the_counts()
    {
        var reversed = Train(History.Reverse());
        reversed.ToJson().ShouldBe(Train(History).ToJson());
    }

    [Fact]
    public void Incremental_updates_equal_a_full_retrain()
    {
        var incremental = Train(History.Take(100));
        foreach (var example in History.Skip(100))
        {
            incremental = incremental.WithExample(example);
        }

        var full = Train(History);
        incremental.ToJson().ShouldBe(full.ToJson());
        Predictions(incremental).ShouldBe(Predictions(full));
    }

    [Fact]
    public void Building_from_empty_one_example_at_a_time_equals_training()
    {
        var model = CategoryLearner.Empty(categories: LabeledHistoryGenerator.Catalog);
        foreach (var example in History.Take(300))
        {
            model = model.WithExample(example);
        }

        model.ToJson().ShouldBe(Train(History.Take(300)).ToJson());
    }

    [Fact]
    public void Removing_examples_equals_training_without_them()
    {
        var full = Train(History);
        var removed = History.Skip(200).Take(50).ToList();
        var model = full;
        foreach (var example in removed)
        {
            model = model.WithoutExample(example);
        }

        model.ToJson().ShouldBe(Train(History.Take(200).Concat(History.Skip(250))).ToJson());
        full.ExampleCount.ShouldBe(History.Count);
        model.ExampleCount.ShouldBe(History.Count - 50);
    }

    [Fact]
    public void Recategorizing_is_remove_then_add()
    {
        var example = Ex("KEY FOOD", Groceries);
        var model = CategoryLearner.Train([.. Repeat(5, "KEY FOOD", Groceries), example], categories: Catalog);
        var changed = model.WithoutExample(example).WithExample(example with { CategoryId = Household });
        changed.PayeeHistory("Key Food").ShouldBe(new Dictionary<Guid, int> { [Groceries] = 5, [Household] = 1 }, ignoreOrder: true);
    }

    [Fact]
    public void Json_round_trip_preserves_the_model_and_its_predictions()
    {
        var model = Train(History);
        var json = model.ToJson();

        var back = LearnerModel.FromJson(json);

        back.ToJson().ShouldBe(json);
        back.ExampleCount.ShouldBe(model.ExampleCount);
        back.Classes.ShouldBe(model.Classes);
        back.Options.ShouldBe(model.Options);
        Predictions(back).ShouldBe(Predictions(model));
        back.WithExample(History[0]).ToJson().ShouldBe(model.WithExample(History[0]).ToJson());
    }

    [Fact]
    public void Json_is_readable_and_keyed_in_ordinal_order()
    {
        var model = CategoryLearner.Train([Ex("KEY FOOD", Groceries, -1234, Checking, new DateOnly(2026, 9, 14))], categories: [new(Groceries, "Groceries", false)]);

        model.ToJson().ShouldBe(
            "{\"format\":\"keel.categoryLearner\",\"version\":1,"
            + "\"options\":{\"smoothing\":1,\"payeePriorStrength\":1,\"contextWeight\":0.5,\"tokenWeight\":1,\"minimumAlternativeConfidence\":0.05},"
            + "\"examples\":1,"
            + "\"categories\":[{\"id\":\"00000000-0000-7000-8000-000000000020\",\"name\":\"Ready to Assign\",\"restricted\":true},"
            + "{\"id\":\"00000000-0000-7000-8000-00000000d001\",\"name\":\"Groceries\",\"restricted\":false}],"
            + "\"classes\":{\"00000000-0000-7000-8000-00000000d001\":1},"
            + "\"payees\":{\"KEY FOOD\":{\"00000000-0000-7000-8000-00000000d001\":1}},"
            + "\"tokens\":{\"FOOD\":{\"00000000-0000-7000-8000-00000000d001\":1},\"KEY\":{\"00000000-0000-7000-8000-00000000d001\":1}},"
            + "\"amountBuckets\":{\"11\":{\"00000000-0000-7000-8000-00000000d001\":1}},"
            + "\"accounts\":{\"00000000-0000-7000-8000-00000000b001\":{\"00000000-0000-7000-8000-00000000d001\":1}},"
            + "\"weekdays\":{\"monday\":{\"00000000-0000-7000-8000-00000000d001\":1}},"
            + "\"directions\":{\"outflow\":{\"00000000-0000-7000-8000-00000000d001\":1}}}");
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("{}", "not a Keel learner model")]
    [InlineData("{\"format\":\"something.else\",\"version\":1}", "not a Keel learner model")]
    [InlineData("{\"format\":\"keel.categoryLearner\",\"version\":2}", "format version 2")]
    [InlineData("{\"format\":\"keel.categoryLearner\",\"version\":1,\"examples\":5,\"classes\":{\"00000000-0000-7000-8000-00000000d001\":1}}", "inconsistent")]
    [InlineData("{\"format\":\"keel.categoryLearner\",\"version\":1,\"examples\":1,\"classes\":{\"not-a-guid\":1}}", "inconsistent")]
    [InlineData("{\"format\":\"keel.categoryLearner\",\"version\":1,\"examples\":0,\"classes\":{},\"directions\":{\"sideways\":{}}}", "inconsistent")]
    public void Unreadable_models_throw_a_format_exception(string json, string message)
    {
        Should.Throw<LearnerModelFormatException>(() => LearnerModel.FromJson(json)).Message.ShouldContain(message);
    }
}
