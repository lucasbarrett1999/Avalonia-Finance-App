using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using Keel.Application.Accounts;
using Keel.Application.Import;
using Keel.Application.Messaging;
using Keel.Domain;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Import;
using Keel.Infrastructure.Import.Csv;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Keel.Benchmarks;

/// <summary>
/// File import through the real pipeline (PRD 11: import 10k rows &lt; 5 s): CSV parse, a first
/// import into an empty account, and a re-import where every row is a duplicate.
/// </summary>
[MemoryDiagnoser]
public class ImportBenchmarks
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keel-bench", Guid.NewGuid().ToString("N"));
    private byte[] _csv = [];
    private ImportService _imports = null!;
    private AccountService _accounts = null!;
    private ParseResult _parsed = null!;
    private Guid _populated;
    private Guid _fresh;

    [GlobalSetup]
    public void Setup()
    {
        Directory.CreateDirectory(_directory);
        var factory = new KeelDbContextFactory();
        new BudgetFileService(factory, NullLogger<BudgetFileService>.Instance)
            .OpenOrCreateAsync(Path.Combine(_directory, "Bench.keel"), CancellationToken.None).GetAwaiter().GetResult();
        var writer = new LedgerWriter(factory, new UndoHistory(), new NullBus(), TimeProvider.System);
        _accounts = new AccountService(factory, writer);
        _imports = new ImportService(factory, writer, [new NoOpImportCategorizationHook()], NullLogger<ImportService>.Instance);
        _csv = TenThousandRows();
        _parsed = CsvImportParser.Parse(_csv, ImportOptions.Default);
        _populated = NewAccount();
        _imports.ImportTransactionsAsync(TransactionSource.File, Batch(_populated), CancellationToken.None).GetAwaiter().GetResult();
    }

    [IterationSetup(Target = nameof(ImportIntoEmptyAccount))]
    public void NewTarget() => _fresh = NewAccount();

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

    [Benchmark]
    public int ParseTenThousandRows() => CsvImportParser.Parse(_csv, ImportOptions.Default).Transactions.Count;

    [Benchmark]
    public async Task<int> ImportIntoEmptyAccount() =>
        (await _imports.ImportTransactionsAsync(TransactionSource.File, Batch(_fresh), CancellationToken.None)).Added;

    [Benchmark]
    public async Task<int> ReimportAllDuplicates() =>
        (await _imports.ImportTransactionsAsync(TransactionSource.File, Batch(_populated), CancellationToken.None)).DuplicatesSkipped;

    private ImportBatch Batch(Guid account) => ImportBatchBuilder.FromParse(account, "USD", _parsed);

    private Guid NewAccount() => _accounts.CreateAccountAsync(
        new CreateAccountRequest("Bench " + Guid.NewGuid().ToString("N")[..8], AccountType.Checking, "USD", new DateOnly(2024, 1, 1), 0),
        CancellationToken.None).GetAwaiter().GetResult().Id;

    private static byte[] TenThousandRows()
    {
        var random = new Random(20260924);
        var text = new StringBuilder("Date,Description,Amount\n");
        var start = new DateOnly(2025, 1, 1);
        for (var i = 0; i < 10_000; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"{start.AddDays(i * 365 / 10_000):yyyy-MM-dd},SQ *MERCHANT {i % 60} #{random.Next(1000, 9999)},{-random.Next(100, 25_000) / 100m:0.00}\n");
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private sealed class NullBus : IMessageBus
    {
        public void Publish<TMessage>(TMessage message)
            where TMessage : class
        {
        }
    }
}
