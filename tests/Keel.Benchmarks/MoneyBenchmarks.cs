using BenchmarkDotNet.Attributes;
using Keel.Domain;

namespace Keel.Benchmarks;

/// <summary>
/// Baseline for integer money arithmetic over a 100k-row ledger. The register-load, budget
/// calculator and import benchmarks of PRD 13 join this project as those features land.
/// </summary>
[MemoryDiagnoser]
public class MoneyBenchmarks
{
    private long[] _amounts = [];

    [Params(100_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        _amounts = new long[Rows];
        for (var i = 0; i < Rows; i++)
        {
            _amounts[i] = random.NextInt64(-500_00, 500_00);
        }
    }

    [Benchmark(Baseline = true)]
    public long SumRawMinorUnits()
    {
        long total = 0;
        foreach (var amount in _amounts)
        {
            total += amount;
        }

        return total;
    }

    [Benchmark]
    public Money SumMoney()
    {
        var total = Money.Zero("USD");
        foreach (var amount in _amounts)
        {
            total += new Money(amount, "USD");
        }

        return total;
    }

    [Benchmark]
    public IReadOnlyList<Money> SplitByPercentages() =>
        new Money(123_456_78, "USD").SplitByPercentages([12.5m, 37.5m, 25m, 25m]);
}
