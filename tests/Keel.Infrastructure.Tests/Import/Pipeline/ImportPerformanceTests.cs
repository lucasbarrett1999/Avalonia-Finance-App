using System.Diagnostics;
using System.Globalization;
using System.Text;
using Keel.Application.Import;
using Keel.Domain;
using Keel.Infrastructure.Tests.Ledger;
using Xunit.Abstractions;
using static Keel.Infrastructure.Tests.Import.Pipeline.ImportKit;

namespace Keel.Infrastructure.Tests.Import.Pipeline;

/// <summary>Timing tests run alone so parallel test classes do not skew them.</summary>
[CollectionDefinition(nameof(ImportPerformanceCollection), DisableParallelization = true)]
public sealed class ImportPerformanceCollection;

/// <summary>PRD 11: import 10k rows in under 5 s, through the parser and the real service.</summary>
[Collection(nameof(ImportPerformanceCollection))]
public sealed class ImportPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Ten_thousand_row_csv_imports_in_under_five_seconds()
    {
        await using var host = await LedgerTestHost.CreateAsync();
        var checking = await host.CheckingAsync();
        var savings = await host.AccountAsync("Savings", AccountType.Savings);
        await host.ImportAsync(savings.Id, new IncomingTransaction(new DateOnly(2025, 7, 3), 50_000, "TRANSFER FROM CHECKING"));
        var bytes = TenThousandRows();

        // Warm up (JIT, EF model, SQLite statements) as a running app would be.
        await host.ImportFileAsync(checking, "warm.csv", Utf8("Date,Description,Amount\n2024-01-02,WARM UP,-1.00\n"));

        var total = Stopwatch.StartNew();
        var parsed = await host.ParseAsync("big.csv", bytes);
        var parseMs = total.ElapsedMilliseconds;
        var batch = Batch(checking, parsed);
        var summary = await host.Get<IImportService>().ImportTransactionsAsync(TransactionSource.File, batch, Ct);
        total.Stop();

        output.WriteLine($"10,000 rows: parse {parseMs} ms, pipeline {total.ElapsedMilliseconds - parseMs} ms, total {total.ElapsedMilliseconds} ms");
        parsed.Transactions.Count.ShouldBe(10_000);
        summary.Added.ShouldBe(10_000);
        summary.TransfersMatched.ShouldBe(1);
        total.ElapsedMilliseconds.ShouldBeLessThan(5_000);

        var again = Stopwatch.StartNew();
        var second = await host.Get<IImportService>().ImportTransactionsAsync(TransactionSource.File, batch, Ct);
        output.WriteLine($"re-import of the same 10,000 rows: {again.ElapsedMilliseconds} ms");
        second.Added.ShouldBe(0);
        second.DuplicatesSkipped.ShouldBe(10_000);
    }

    /// <summary>A deterministic year of bank rows (about 27 a day) over 60 merchants.</summary>
    internal static byte[] TenThousandRows()
    {
        var random = new Random(20260924);
        var text = new StringBuilder("Date,Description,Amount\n");
        var start = new DateOnly(2025, 1, 1);
        for (var i = 0; i < 10_000; i++)
        {
            var date = start.AddDays(i * 365 / 10_000);
            var amount = i == 5_000 ? -50_000 : -random.Next(100, 25_000);
            var payee = i == 5_000 ? "ONLINE TRANSFER TO SAVINGS" : $"SQ *MERCHANT {i % 60} #{random.Next(1000, 9999)}";
            text.Append(CultureInfo.InvariantCulture, $"{date:yyyy-MM-dd},{payee},{amount / 100m:0.00}\n");
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }
}
