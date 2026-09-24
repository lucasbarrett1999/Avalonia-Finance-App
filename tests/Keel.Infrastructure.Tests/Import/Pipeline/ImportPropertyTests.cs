using CsCheck;
using Keel.Application.Import;
using Keel.Application.Ledger;
using Keel.Domain;
using Keel.Infrastructure.Tests.Ledger;
using static Keel.Infrastructure.Tests.Import.Pipeline.ImportKit;

namespace Keel.Infrastructure.Tests.Import.Pipeline;

/// <summary>
/// PRD 13: importing any batch twice yields no new rows, through the real service and database
/// (manual entries, identical rows, repeated FITIDs and pending ids included).
/// </summary>
public sealed class ImportPropertyTests : IAsyncLifetime
{
    private static readonly DateOnly Start = new(2026, 3, 1);

    private static readonly string[] Payees =
    [
        "SQ *BLUE BOTTLE", "Blue Bottle", "STARBUCKS #1234", "Starbucks", "AMZN Mktp US*2K4AB1CD2",
        "Amazon", "SHELL OIL 57444212500", "Shell", "TRADER JOE'S #552", "Landlord LLC", "", "POS DEBIT 99999",
    ];

    private static readonly Gen<IncomingTransaction> GenRow =
        Gen.Select(
            Gen.Int[0, 20],
            Gen.OneOfConst(-1250L, -500L, -150000L, 2500L, -1L, 0L),
            Gen.OneOfConst(Payees),
            Gen.Int[0, 8].Select(i => i < 3 ? $"FIT{i}" : null),
            Gen.Int[0, 8].Select(i => i < 2 ? $"FIT{i}" : null),
            Gen.Bool)
        .Select((day, amount, payee, id, pending, isPending) => new IncomingTransaction(Start.AddDays(day), amount, payee, null, id, pending, isPending && id is not null));

    private static readonly Gen<(int Day, long Amount, string Payee)> GenManual =
        Gen.Select(Gen.Int[0, 20], Gen.OneOfConst(-1250L, -500L, -150000L, 2500L), Gen.OneOfConst(Payees));

    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Importing_any_batch_twice_adds_no_rows_the_second_time()
    {
        var service = _host.Get<IImportService>();
        var cases = Gen.Select(GenRow.List[0, 30], GenManual.List[0, 6]).Array[25].Single();
        var failures = new List<string>();
        var n = 0;
        foreach (var (batch, manual) in cases)
        {
            var account = await _host.CheckingAsync($"Account {n++}", opening: 0);
            foreach (var (day, amount, payee) in manual)
            {
                await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, account.Id, Start.AddDays(day), amount, payee, null, null), Ct);
            }

            var before = await _host.CountAsync(account.Id);
            var first = await service.ImportTransactionsAsync(TransactionSource.File, new ImportBatch(account.Id, batch), Ct);
            var afterFirst = await _host.CountAsync(account.Id);
            var second = await service.ImportTransactionsAsync(TransactionSource.File, new ImportBatch(account.Id, batch), Ct);
            var afterSecond = await _host.CountAsync(account.Id);

            afterFirst.ShouldBe(before + first.Added);
            if (second.Added != 0 || afterSecond != afterFirst)
            {
                failures.Add($"batch of {batch.Count} rows with {manual.Count} manual entries added {second.Added} on the second import: "
                    + string.Join("; ", batch.Select(b => $"{b.Date:dd} {b.Amount} '{b.PayeeRaw}' {b.ProviderTransactionId}/{b.ProviderPendingId}/{b.IsPending}"))
                    + " || manual: " + string.Join("; ", manual.Select(m => $"{m.Day} {m.Amount} '{m.Payee}'")));
            }
        }

        failures.ShouldBeEmpty();
    }
}
