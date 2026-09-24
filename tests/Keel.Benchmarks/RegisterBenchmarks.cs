using BenchmarkDotNet.Attributes;
using Keel.Application.Ledger;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Keel.Benchmarks;

/// <summary>
/// Register load over the deterministic 100k-transaction fixture (PRD 11: register open
/// &lt; 500 ms at 100k rows, warm database). "Open" is what the register does on navigation:
/// count, header summary, and the first page of 200 rows with running balances.
/// </summary>
[MemoryDiagnoser]
public class RegisterBenchmarks
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keel-bench", Guid.NewGuid().ToString("N"));
    private RegisterQuery _register = null!;
    private RegisterFilter _all = null!;
    private RegisterFilter _visa = null!;
    private int _total;

    [GlobalSetup]
    public void Setup()
    {
        Directory.CreateDirectory(_directory);
        var factory = new KeelDbContextFactory();
        new BudgetFileService(factory, NullLogger<BudgetFileService>.Instance)
            .OpenOrCreateAsync(Path.Combine(_directory, "Bench.keel"), CancellationToken.None).GetAwaiter().GetResult();
        var fixture = LedgerFixtureGenerator.GenerateAsync(factory, new LedgerFixtureOptions(), CancellationToken.None).GetAwaiter().GetResult();
        _register = new RegisterQuery(factory);
        _all = new RegisterFilter();
        _visa = new RegisterFilter(fixture.Accounts["Visa Rewards"]);
        _total = _register.CountAsync(_all, CancellationToken.None).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Benchmark(Baseline = true)]
    public Task<int> OpenAllAccounts() => OpenAsync(_all);

    [Benchmark]
    public Task<int> OpenOneAccount() => OpenAsync(_visa);

    [Benchmark]
    public async Task<int> LastPageAllAccounts() =>
        (await _register.GetPageAsync(_all, RegisterSort.Default, _total - IRegisterQuery.PageSize, IRegisterQuery.PageSize, CancellationToken.None)).Count;

    [Benchmark]
    public async Task<int> FirstPageSortedByPayee() =>
        (await _register.GetPageAsync(_all, new RegisterSort(RegisterSortColumn.Payee, false), 0, IRegisterQuery.PageSize, CancellationToken.None)).Count;

    [Benchmark]
    public async Task<int> SearchFirstPage() =>
        (await _register.GetPageAsync(_all with { Search = "amount:>100 category:groceries" }, RegisterSort.Default, 0, IRegisterQuery.PageSize, CancellationToken.None)).Count;

    private async Task<int> OpenAsync(RegisterFilter filter)
    {
        var count = await _register.CountAsync(filter, CancellationToken.None);
        await _register.GetSummaryAsync(filter.AccountId, CancellationToken.None);
        var page = await _register.GetPageAsync(filter, RegisterSort.Default, 0, IRegisterQuery.PageSize, CancellationToken.None);
        return count + page.Count;
    }
}
