using System.Diagnostics;
using Keel.Domain.Categorization;
using Xunit.Abstractions;

namespace Keel.Domain.Tests.Categorization;

/// <summary>Training time for 100k examples and suggestion latency (target &lt; 1 ms after warm-up).</summary>
public class LearnerPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void Trains_100k_examples_and_suggests_in_under_a_millisecond()
    {
        var history = LabeledHistoryGenerator.Generate(months: 150, seed: 5, extraPayees: 400).Take(100_000).ToList();
        history.Count.ShouldBe(100_000);
        var probes = history.Where((_, i) => i % 97 == 0).Select(e => e.Transaction with { CategoryId = null }).ToList();

        CategoryLearner.Train(history.Take(5_000), categories: LabeledHistoryGenerator.Catalog); // warm-up (JIT)
        var trainTimes = new List<double>();
        LearnerModel model = null!;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            model = CategoryLearner.Train(history, categories: LabeledHistoryGenerator.Catalog);
            sw.Stop();
            trainTimes.Add(sw.Elapsed.TotalMilliseconds);
        }

        foreach (var probe in probes.Take(200))
        {
            model.Suggest(probe, 5); // warm-up
        }

        var suggest = Stopwatch.StartNew();
        var suggested = 0;
        foreach (var probe in probes)
        {
            suggested += model.Suggest(probe, 5).Count > 0 ? 1 : 0;
        }

        suggest.Stop();
        var perSuggestion = suggest.Elapsed.TotalMilliseconds / probes.Count;

        var update = Stopwatch.StartNew();
        var updated = model;
        foreach (var example in history.Take(1_000))
        {
            updated = updated.WithExample(example);
        }

        update.Stop();
        var perUpdate = update.Elapsed.TotalMilliseconds / 1_000;

        var json = Stopwatch.StartNew();
        var text = model.ToJson();
        var back = LearnerModel.FromJson(text);
        json.Stop();

        trainTimes.Sort();
        output.WriteLine($"Train 100k examples ({model.PayeeCount} payees, {model.Classes.Length} categories): median {trainTimes[1]:F0} ms (min {trainTimes[0]:F0}, max {trainTimes[2]:F0})");
        output.WriteLine($"Suggest: {perSuggestion * 1000:F1} µs per suggestion over {probes.Count} probes ({suggested} with a suggestion)");
        output.WriteLine($"WithExample: {perUpdate * 1000:F1} µs per incremental update");
        output.WriteLine($"JSON: {text.Length / 1024} KiB, serialize + deserialize {json.Elapsed.TotalMilliseconds:F0} ms");

        back.ExampleCount.ShouldBe(100_000);
        perSuggestion.ShouldBeLessThan(1.0);
        perUpdate.ShouldBeLessThan(1.0);
        trainTimes[1].ShouldBeLessThan(15_000);
    }
}
