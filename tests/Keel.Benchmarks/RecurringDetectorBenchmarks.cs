using BenchmarkDotNet.Attributes;
using Keel.Domain.Recurring;
using Keel.Domain.Tests.Recurring;

namespace Keel.Benchmarks;

/// <summary>
/// M5: <see cref="RecurringDetector.Detect"/> over about 120k transactions and 2k payees
/// (target &lt; 500 ms). Same deterministic ledger as the performance test.
/// </summary>
[MemoryDiagnoser]
public class RecurringDetectorBenchmarks
{
    private static readonly DateOnly AsOf = new(2026, 9, 24);
    private List<RecurringTransaction> _transactions = null!;
    private IReadOnlyList<DetectedRecurringItem> _detected = null!;

    [Params(2_000)]
    public int Payees { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _transactions = RecurringFixtureGenerator.Generate(Payees, AsOf, seed: 7).Transactions;
        _detected = RecurringDetector.Detect(_transactions, AsOf);
    }

    [Benchmark]
    public IReadOnlyList<DetectedRecurringItem> Detect() => RecurringDetector.Detect(_transactions, AsOf);

    [Benchmark]
    public IReadOnlyList<RecurringDecision> ReconcileAgainstEmpty() => RecurringReconciler.Reconcile(_detected, []);
}
