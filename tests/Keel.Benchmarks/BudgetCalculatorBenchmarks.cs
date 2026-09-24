using BenchmarkDotNet.Attributes;
using Keel.Domain.Budgeting;
using Keel.Domain.Tests.Budgeting;

namespace Keel.Benchmarks;

/// <summary>
/// PRD 6.4.9 / 13: <see cref="BudgetCalculator.Compute"/> over 36 months × 60 categories × 8 accounts
/// of pre-aggregated activity (target &lt; 200 ms). Same deterministic input as the performance test.
/// </summary>
[MemoryDiagnoser]
public class BudgetCalculatorBenchmarks
{
    private static readonly DateOnly From = new(2024, 1, 1);
    private BudgetInput _input = null!;

    [Params(36)]
    public int Months { get; set; }

    [GlobalSetup]
    public void Setup() => _input = BudgetInputGenerator.Generate(Months, categories: 60, accounts: 8, seed: 7);

    [Benchmark]
    public BudgetSnapshot ComputeRange() => BudgetCalculator.Compute(_input, From, From.AddMonths(Months - 1));

    [Benchmark]
    public BudgetMonthResult ComputeLastMonth() => BudgetCalculator.ComputeMonth(_input, From.AddMonths(Months - 1));
}
