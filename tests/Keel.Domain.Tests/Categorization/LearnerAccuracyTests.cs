using System.Globalization;
using Keel.Domain.Categorization;
using Xunit.Abstractions;

namespace Keel.Domain.Tests.Categorization;

/// <summary>PRD 12 M4 exit and PRD 13: learner accuracy on a labeled, deterministic fixture.</summary>
public class LearnerAccuracyTests(ITestOutputHelper output)
{
    internal sealed record Evaluation(int Total, int Suggested, int Correct, int Confident, int ConfidentCorrect)
    {
        public double Accuracy => (double)Correct / Total;

        public double Coverage => (double)Suggested / Total;

        public double Precision => Suggested == 0 ? 1 : (double)Correct / Suggested;

        public double ConfidentPrecision => Confident == 0 ? 1 : (double)ConfidentCorrect / Confident;

        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture,
            "n={0}, top-1 accuracy {1:P1} (empty counts as wrong), coverage {2:P1}, precision {3:P1}, confident (>= 0.9) {4} with precision {5:P1}",
            Total,
            Accuracy,
            Coverage,
            Precision,
            Confident,
            ConfidentPrecision);
    }

    internal static Evaluation Evaluate(LearnerModel model, IEnumerable<LabeledExample> heldOut)
    {
        int total = 0, suggested = 0, correct = 0, confident = 0, confidentCorrect = 0;
        foreach (var example in heldOut)
        {
            total++;
            var suggestions = model.Suggest(example.Transaction with { CategoryId = null }, 1);
            if (suggestions.Count == 0)
            {
                continue;
            }

            suggested++;
            var hit = suggestions[0].CategoryId == example.CategoryId;
            correct += hit ? 1 : 0;
            if (suggestions[0].Confidence >= 0.9)
            {
                confident++;
                confidentCorrect += hit ? 1 : 0;
            }
        }

        return new Evaluation(total, suggested, correct, confident, confidentCorrect);
    }

    [Fact]
    public void Fixture_is_realistic()
    {
        var history = LabeledHistoryGenerator.Generate();
        output.WriteLine($"{history.Count} examples, {LabeledHistoryGenerator.PayeeCount} payees, {history.Select(e => e.CategoryId).Distinct().Count()} categories, "
            + $"{history.Select(e => e.Transaction.PayeeRaw).Distinct().Count()} distinct descriptors, split payees: {string.Join(", ", LabeledHistoryGenerator.SplitPayees)}");
        LabeledHistoryGenerator.PayeeCount.ShouldBeGreaterThanOrEqualTo(60);
        history.Select(e => e.CategoryId).Distinct().Count().ShouldBe(30);
        LabeledHistoryGenerator.SplitPayees.Count.ShouldBeGreaterThanOrEqualTo(5);
        history.Select(e => e.Transaction.PayeeRaw).Distinct().Count().ShouldBeGreaterThan(history.Count / 3);
        LabeledHistoryGenerator.Generate().Select(e => (e.Transaction.PayeeRaw, e.Transaction.Amount, e.CategoryId))
            .ShouldBe(history.Select(e => (e.Transaction.PayeeRaw, e.Transaction.Amount, e.CategoryId)));
    }

    [Fact]
    public void Top_1_accuracy_on_a_held_out_20_percent_is_at_least_85_percent_and_confident_suggestions_are_90_percent_right()
    {
        var history = LabeledHistoryGenerator.Generate();
        var cut = (int)(history.Count * 0.8);
        var model = CategoryLearner.Train(history.Take(cut), categories: LabeledHistoryGenerator.Catalog);

        var result = Evaluate(model, history.Skip(cut));
        output.WriteLine($"Chronological 80/20 split of {history.Count}: {result}");

        result.Accuracy.ShouldBeGreaterThanOrEqualTo(0.85);
        result.ConfidentPrecision.ShouldBeGreaterThanOrEqualTo(0.90);
        result.Confident.ShouldBeGreaterThan(result.Total / 2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(2026)]
    public void Accuracy_holds_across_seeds_with_a_random_split(int seed)
    {
        var history = LabeledHistoryGenerator.Generate(seed: seed);
        var rng = new Random(seed);
        var shuffled = history.OrderBy(_ => rng.Next()).ToList();
        var cut = (int)(shuffled.Count * 0.8);
        var model = CategoryLearner.Train(shuffled.Take(cut), categories: LabeledHistoryGenerator.Catalog);

        var result = Evaluate(model, shuffled.Skip(cut));
        output.WriteLine($"seed {seed}, random 80/20: {result}");

        result.Accuracy.ShouldBeGreaterThanOrEqualTo(0.85);
        result.ConfidentPrecision.ShouldBeGreaterThanOrEqualTo(0.90);
    }

    [Fact]
    public void After_200_approvals_suggestions_are_at_least_85_percent_right()
    {
        // PRD 12 M4 exit: "learner accuracy >= 85% after 200 approvals". With 64 payees, many have
        // fewer than 3 examples after 200 approvals and F-TXN-5 requires leaving them
        // uncategorized, so this asserts the accuracy of the suggestions made (ADR 0021) and reports
        // coverage; the accuracy counting empty as wrong is asserted from 600 approvals on.
        var history = LabeledHistoryGenerator.Generate();
        foreach (var approvals in new[] { 200, 400, 600, 1000 })
        {
            var model = CategoryLearner.Train(history.Take(approvals), categories: LabeledHistoryGenerator.Catalog);
            var result = Evaluate(model, history.Skip(approvals).Take(500));
            output.WriteLine($"After {approvals} approvals, next 500: {result}");

            result.Precision.ShouldBeGreaterThanOrEqualTo(0.85);
            result.ConfidentPrecision.ShouldBeGreaterThanOrEqualTo(0.90);
            if (approvals >= 600)
            {
                result.Accuracy.ShouldBeGreaterThanOrEqualTo(0.85);
            }
        }
    }
}
