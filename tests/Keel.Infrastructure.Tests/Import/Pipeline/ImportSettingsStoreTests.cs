using Keel.Application.Import;
using Keel.Domain.Entities;
using Keel.Infrastructure.Import;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;
using static Keel.Infrastructure.Tests.Import.Pipeline.ImportKit;

namespace Keel.Infrastructure.Tests.Import.Pipeline;

public sealed class ImportSettingsStoreTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private IImportSettingsStore Store => _host.Get<IImportSettingsStore>();

    [Fact]
    public async Task Csv_mapping_is_remembered_per_account_and_survives_reopening()
    {
        var checking = await _host.CheckingAsync();
        var card = await _host.CheckingAsync("Card");
        var parsed = await _host.ParseAsync("european-semicolon.csv", Fixture("european-semicolon.csv"));
        var layout = parsed.CsvLayout!;
        var edited = layout.Mapping with { SignConvention = CsvSignConvention.OutflowPositive, SkipRows = 1, MemoColumn = null };

        await Store.SaveCsvMappingAsync(checking.Id, new RememberedCsvMapping(edited, layout.Headers), Ct);

        (await Store.GetCsvMappingAsync(card.Id, Ct)).ShouldBeNull();
        var remembered = (await Store.GetCsvMappingAsync(checking.Id, Ct))!;
        remembered.Mapping.ShouldBe(edited);
        remembered.Headers.ShouldBe(layout.Headers);
        remembered.Fits(layout.Headers.Select(h => " " + h.ToUpperInvariant()).ToList()).ShouldBeTrue();
        remembered.Fits(["Date", "Amount"]).ShouldBeFalse();

        // Stored in the budget file itself, readable by a fresh context.
        await using var db = _host.Db();
        var row = await db.Settings.SingleAsync(s => s.Key == ImportSettingsStore.MappingKey(checking.Id));
        row.ValueJson.ShouldContain("\"OutflowPositive\"");
    }

    [Fact]
    public async Task Remembered_mapping_parses_the_file_identically()
    {
        var account = await _host.CheckingAsync();
        var bytes = Fixture("credit-union-debit-credit.csv");
        var detected = await _host.ParseAsync("credit-union-debit-credit.csv", bytes);
        await Store.SaveCsvMappingAsync(account.Id, new RememberedCsvMapping(detected.CsvLayout!.Mapping, detected.CsvLayout.Headers), Ct);

        var remembered = (await Store.GetCsvMappingAsync(account.Id, Ct))!;
        var again = await _host.ParseAsync("credit-union-debit-credit.csv", bytes, ImportOptions.Default with { CsvMapping = remembered.Mapping });
        again.Transactions.Select(Key).ShouldBe(detected.Transactions.Select(Key));
    }

    [Fact]
    public async Task Last_folder_is_remembered_per_account_and_bad_values_read_as_nothing()
    {
        var account = await _host.CheckingAsync();
        (await Store.GetLastFolderAsync(account.Id, Ct)).ShouldBeNull();
        await Store.SaveLastFolderAsync(account.Id, "/home/me/Downloads", Ct);
        await Store.SaveLastFolderAsync(account.Id, "/home/me/Bank", Ct);
        (await Store.GetLastFolderAsync(account.Id, Ct)).ShouldBe("/home/me/Bank");

        await using (var db = _host.Db())
        {
            db.Settings.Add(new Setting { Key = ImportSettingsStore.MappingKey(account.Id), ValueJson = "{not json" });
            await db.SaveChangesAsync();
        }

        (await Store.GetCsvMappingAsync(account.Id, Ct)).ShouldBeNull();
    }

    private static (DateOnly, long, string, string?, bool, string?) Key(ParsedTransaction t) =>
        (t.Date, t.Amount, t.PayeeRaw, t.Memo, t.IsPending, t.CheckNumber);
}
