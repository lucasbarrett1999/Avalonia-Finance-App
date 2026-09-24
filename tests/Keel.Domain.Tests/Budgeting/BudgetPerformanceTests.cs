using System.Diagnostics;
using Keel.Domain.Budgeting;
using Xunit.Abstractions;

namespace Keel.Domain.Tests.Budgeting;

/// <summary>PRD 6.4.9: all categories over 36 months from pre-aggregated activity in &lt; 200 ms.</summary>
public class BudgetPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void Computes_36_months_of_60_categories_over_8_accounts_in_under_200_ms()
    {
        var input = BudgetInputGenerator.Generate(months: 36, categories: 60, accounts: 8, seed: 7);
        input.Activity.Count.ShouldBeGreaterThanOrEqualTo(36 * 60 * 8);
        var from = new DateOnly(2024, 1, 1);
        var to = from.AddMonths(35);

        for (var i = 0; i < 3; i++)
        {
            BudgetCalculator.Compute(input, from, to); // warm-up (JIT, tiering)
        }

        var timings = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var snapshot = BudgetCalculator.Compute(input, from, to);
            stopwatch.Stop();
            snapshot.Months.Count.ShouldBe(36);
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var median = timings[timings.Count / 2];
        output.WriteLine($"BudgetCalculator.Compute 36 months x 60 categories x 8 accounts ({input.Activity.Count} activity rows): median {median:F2} ms, min {timings[0]:F2} ms, max {timings[^1]:F2} ms");
        median.ShouldBeLessThan(200);
    }
}
