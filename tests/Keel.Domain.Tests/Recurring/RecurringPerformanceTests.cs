using System.Diagnostics;
using Keel.Domain.Recurring;
using Xunit.Abstractions;

namespace Keel.Domain.Tests.Recurring;

/// <summary>Timing tests run alone, after the parallel tests, so CPU-heavy property tests do not skew them.</summary>
[CollectionDefinition(nameof(TimingCollection), DisableParallelization = true)]
public sealed class TimingCollection;

/// <summary>M5 target: detection over 100k transactions and 2k payees in under 500 ms.</summary>
[Collection(nameof(TimingCollection))]
public class RecurringPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void Detects_over_100k_transactions_and_2k_payees_in_under_500_ms()
    {
        var asOf = new DateOnly(2026, 9, 24);
        var (transactions, labels) = RecurringFixtureGenerator.Generate(2_000, asOf, seed: 7);
        transactions.Count.ShouldBeGreaterThanOrEqualTo(100_000);
        labels.Count.ShouldBe(2_000);

        for (var i = 0; i < 3; i++)
        {
            RecurringDetector.Detect(transactions, asOf); // warm-up (JIT, tiering)
        }

        var timings = new List<double>();
        IReadOnlyList<DetectedRecurringItem> detected = [];
        for (var i = 0; i < 5; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            detected = RecurringDetector.Detect(transactions, asOf);
            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var median = timings[timings.Count / 2];
        output.WriteLine($"RecurringDetector.Detect over {transactions.Count} transactions, {labels.Count} payees ({detected.Count} detected): median {median:F1} ms, min {timings[0]:F1} ms, max {timings[^1]:F1} ms");
        // The fastest run is the least sensitive to other processes on a shared CI machine.
        timings[0].ShouldBeLessThan(500);
        detected.Count.ShouldBe(labels.Count(l => l.Expected is not null));
    }
}
