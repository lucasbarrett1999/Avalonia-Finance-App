using BenchmarkDotNet.Attributes;
using Keel.Domain.Categorization;
using Keel.Domain.Rules;
using Keel.Domain.Tests.Categorization;

namespace Keel.Benchmarks;

/// <summary>
/// F-TXN-5: training the categorization learner on 100k approved examples, one suggestion
/// (target &lt; 1 ms), and one incremental update. Same deterministic fixture as
/// <c>LearnerPerformanceTests</c>.
/// </summary>
[MemoryDiagnoser]
public class CategoryLearnerBenchmarks
{
    private List<LabeledExample> _history = null!;
    private LearnerModel _model = null!;
    private TransactionSnapshot[] _probes = null!;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _history = LabeledHistoryGenerator.Generate(months: 150, seed: 5, extraPayees: 400).Take(100_000).ToList();
        _model = CategoryLearner.Train(_history, categories: LabeledHistoryGenerator.Catalog);
        _probes = _history.Where((_, i) => i % 97 == 0).Select(e => e.Transaction with { CategoryId = null }).ToArray();
    }

    [Benchmark]
    public LearnerModel Train100k() => CategoryLearner.Train(_history, categories: LabeledHistoryGenerator.Catalog);

    [Benchmark]
    public IReadOnlyList<CategorySuggestion> Suggest() => _model.Suggest(_probes[_next++ % _probes.Length], 5);

    [Benchmark]
    public LearnerModel WithExample() => _model.WithExample(_history[_next++ % _history.Count]);
}
