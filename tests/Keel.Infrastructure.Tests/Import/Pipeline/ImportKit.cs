using System.Text;
using Keel.Application.Accounts;
using Keel.Application.Import;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Import.Pipeline;

/// <summary>Helpers for driving the real import pipeline over a <see cref="LedgerTestHost"/>.</summary>
internal static class ImportKit
{
    public static CancellationToken Ct => CancellationToken.None;

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n"));

    public static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(FixtureJson.OutputDirectory, name));

    /// <summary>Parses with the parser the app's DI graph resolves for the file.</summary>
    public static async Task<ParseResult> ParseAsync(this LedgerTestHost host, string fileName, byte[] bytes, ImportOptions? options = null)
    {
        var parser = host.Get<IFileImportParserResolver>().Resolve(fileName, bytes.AsSpan(0, Math.Min(bytes.Length, 4096)));
        parser.ShouldNotBeNull(fileName);
        await using var stream = new MemoryStream(bytes);
        return await parser.ParseAsync(stream, options ?? ImportOptions.Default, Ct);
    }

    public static ImportBatch Batch(AccountDto account, ParseResult result, IReadOnlyDictionary<int, ImportRowOverride>? overrides = null) =>
        ImportBatchBuilder.FromParse(account.Id, account.Balance.Currency, result) with { Overrides = overrides };

    /// <summary>Parses and imports a file into <paramref name="account"/>.</summary>
    public static async Task<ImportSummary> ImportFileAsync(this LedgerTestHost host, AccountDto account, string fileName, byte[] bytes, ImportOptions? options = null)
    {
        var result = await host.ParseAsync(fileName, bytes, options);
        return await host.Get<IImportService>().ImportTransactionsAsync(TransactionSource.File, Batch(account, result), Ct);
    }

    public static Task<ImportSummary> ImportAsync(this LedgerTestHost host, Guid accountId, params IncomingTransaction[] rows) =>
        host.Get<IImportService>().ImportTransactionsAsync(TransactionSource.File, new ImportBatch(accountId, rows), Ct);

    /// <summary>Non-deleted imported rows of an account, in (date, id) order.</summary>
    public static async Task<List<Transaction>> ImportedAsync(this LedgerTestHost host, Guid accountId)
    {
        await using var db = host.Db();
        return await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == accountId && t.Source != TransactionSource.System)
            .OrderBy(t => t.Date).ThenBy(t => t.Id)
            .ToListAsync();
    }

    public static async Task<int> CountAsync(this LedgerTestHost host, Guid accountId)
    {
        await using var db = host.Db();
        return await db.Transactions.CountAsync(t => t.AccountId == accountId && t.Source != TransactionSource.System);
    }
}
