using System.Diagnostics;
using System.Text.Json.Nodes;
using Keel.Application.Files;
using Keel.Application.Portability;
using Keel.Domain.Entities;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;
using Target = Keel.Domain.Entities.Target;

namespace Keel.Infrastructure.Tests.Portability;

/// <summary>
/// F-REP-6: the JSON bundle re-imports into a fresh budget file losslessly. The source is the ledger fixture
/// plus every other PRD 6.2 table; equality is checked table by table on the stored values (row counts and a
/// canonical hash), independently of the export code.
/// </summary>
public sealed class BundleRoundTripTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private LedgerTestHost _host = null!;
    private LedgerFixture _fixture = null!;

    private static CancellationToken Ct => CancellationToken.None;

    private IDataExportService Export => _host.Get<IDataExportService>();

    private IBundleImportService Bundles => _host.Get<IBundleImportService>();

    public async Task InitializeAsync()
    {
        _host = await LedgerTestHost.CreateAsync();
        _fixture = await LedgerFixtureGenerator.GenerateAsync(_host.Factory, new LedgerFixtureOptions(3_000, Seed: 7), Ct);
        await PortabilityKit.AddEverythingElseAsync(_host.Factory, _fixture, _host.Get<IDataDirectory>().AttachmentsDirectoryFor(_host.FilePath));
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _temp.Dispose();
    }

    [Fact]
    public async Task A_bundle_reimports_into_a_new_file_table_by_table_identical()
    {
        var bundle = _temp.File("export.json");
        var info = await Export.ExportBundleAsync(bundle, Ct);
        info.Version.ShouldBe(BundleFormat.Version);
        info.Count(nameof(Transaction)).ShouldBe(3_000);
        info.AttachmentFiles.ShouldBe(1);
        info.SourceFile.ShouldBe("Ledger.keel");

        var target = _temp.File("Restored.keel");
        var result = await Bundles.ImportIntoNewFileAsync(bundle, target, Ct);
        result.Count(nameof(Transaction)).ShouldBe(3_000);
        result.AttachmentFiles.ShouldBe(1);

        var source = await PortabilityKit.HashTablesAsync(_host.FilePath);
        var restored = await PortabilityKit.HashTablesAsync(target);
        restored.Keys.Order().ShouldBe(source.Keys.Order());
        foreach (var (table, hash) in source)
        {
            output.WriteLine($"{table}: {hash.Rows} rows");
            restored[table].ShouldBe(hash, $"table {table}");
        }

        source[nameof(Transaction)].Rows.ShouldBe(3_000, "the soft-deleted row is carried too");
        foreach (var table in new[] { nameof(Rule), nameof(Target), nameof(ScheduledTransaction), nameof(RecurringItem), nameof(Tag), nameof(TransactionTag), nameof(Alert), nameof(Reconciliation), nameof(SyncConnection), nameof(Attachment), nameof(BudgetAssignment), nameof(Setting) })
        {
            source[table].Rows.ShouldBeGreaterThan(0, table);
        }

        // Attachment files come along; derived setting keys stay behind.
        var attachments = _host.Get<IDataDirectory>().AttachmentsDirectoryFor(target);
        Directory.EnumerateFiles(attachments, "*", SearchOption.AllDirectories).ShouldHaveSingleItem();
        await using var db = KeelDbContextFactory.CreateForFile(target);
        (await db.Settings.AnyAsync(s => s.Key == Keel.Infrastructure.Categorization.LearnerService.SettingKey)).ShouldBeFalse();
        (await db.Settings.AnyAsync(s => s.Key == "recurring.importAuditWatermark")).ShouldBeFalse();

        // The import recorded one audit row per imported row (ADR 0052 style).
        var audited = await db.AuditEvents.CountAsync();
        audited.ShouldBe((int)result.Rows.Values.Sum());
    }

    [Fact]
    public async Task The_restored_file_opens_with_the_real_services()
    {
        var bundle = _temp.File("export.json");
        await Export.ExportBundleAsync(bundle, Ct);
        var target = _temp.File("Opened.keel");
        await Bundles.ImportIntoNewFileAsync(bundle, target, Ct);

        await using var opened = await LedgerTestHost.CreateAsync();
        var info = await opened.Get<IBudgetFileService>().OpenOrCreateAsync(target, Ct);
        info.AppliedMigrations.ShouldBeEmpty();
        var sourceAccounts = await _host.Accounts.GetAccountsAsync(true, Ct);
        var restoredAccounts = await opened.Accounts.GetAccountsAsync(true, Ct);
        restoredAccounts.Select(a => (a.Id, a.Name, a.Balance)).ShouldBe(sourceAccounts.Select(a => (a.Id, a.Name, a.Balance)));
        var filter = new Keel.Application.Ledger.RegisterFilter();
        (await opened.Register.CountAsync(filter, Ct)).ShouldBe(await _host.Register.CountAsync(filter, Ct));
        BackupArchive.VerifyDatabase(target);
    }

    [Fact]
    public async Task The_header_is_read_without_the_rows()
    {
        var bundle = _temp.File("export.json");
        var written = await Export.ExportBundleAsync(bundle, Ct);
        var info = await Bundles.ReadInfoAsync(bundle, Ct);
        info.Counts.ShouldBe(written.Counts);
        info.Schema.ShouldNotBeNull().ShouldBe(written.Schema);
        info.ExportedAt.Kind.ShouldBe(DateTimeKind.Utc);
        info.AppVersion.ShouldNotBeNullOrWhiteSpace();
        info.Count(nameof(AuditEvent)).ShouldBe(0, "the audit history is not part of the bundle");
    }

    [Fact]
    public async Task A_newer_bundle_version_is_refused_clearly()
    {
        var path = _temp.File("newer.json");
        await File.WriteAllTextAsync(path, """{"format":"keel-export","version":2,"tables":[]}""");
        (await Should.ThrowAsync<BundleException>(() => Bundles.ReadInfoAsync(path, Ct))).Code.ShouldBe(BundleError.TooNew);
        var target = _temp.File("never.keel");
        (await Should.ThrowAsync<BundleException>(() => Bundles.ImportIntoNewFileAsync(path, target, Ct))).Code.ShouldBe(BundleError.TooNew);
        File.Exists(target).ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"format":"keel-export","version":1,"schema":"29990101000000_FromTheFuture","counts":{},"attachmentFiles":0,"tables":[]}""", BundleError.TooNew)]
    [InlineData("""{"format":"keel-export","version":1,"counts":{},"attachmentFiles":0,"tables":[{"entity":"Spaceship","columns":["Id"],"rows":[]}]}""", BundleError.TooNew)]
    [InlineData("""{"format":"keel-export","version":1,"counts":{},"attachmentFiles":0,"tables":[{"entity":"Tag","columns":["Id","Name","Colour"],"rows":[]}]}""", BundleError.TooNew)]
    [InlineData("""{"format":"something-else","version":1,"tables":[]}""", BundleError.NotABundle)]
    [InlineData("Date,Payee,Amount\n2026-01-01,Grocer,-5.00\n", BundleError.NotABundle)]
    [InlineData("""{"format":"keel-export","version":1,"counts":{"Tag":2},"attachmentFiles":0,"tables":[{"entity":"Tag","columns":["Id","Name"],"rows":[["0192a000-0000-7000-8000-000000000001","A"]]}]}""", BundleError.NotABundle)]
    [InlineData("""{"format":"keel-export","version":1,"counts":{"Tag":1},"attachmentFiles":0,"tables":[{"entity":"Tag","columns":["Id","Name"],"rows":[["0192a000-0000-7000-8000-000000000001",""" + "\n", BundleError.NotABundle)]
    public async Task Bad_bundles_are_refused_and_leave_no_file(string content, BundleError expected)
    {
        var path = _temp.File("bad.json");
        await File.WriteAllTextAsync(path, content);
        var target = _temp.File("bad.keel");
        var error = await Should.ThrowAsync<BundleException>(() => Bundles.ImportIntoNewFileAsync(path, target, Ct));
        error.Code.ShouldBe(expected, error.Message);
        File.Exists(target).ShouldBeFalse();
    }

    [Fact]
    public async Task A_row_that_refers_to_a_missing_row_is_refused_before_commit()
    {
        var bundle = await TamperedAsync(tables =>
        {
            var transactions = Table(tables, nameof(Transaction));
            var account = Column(transactions, nameof(Transaction.AccountId));
            ((JsonArray)transactions["rows"]!)[5]![account] = Guid.CreateVersion7().ToString();
        });

        var target = _temp.File("broken.keel");
        var error = await Should.ThrowAsync<BundleException>(() => Bundles.ImportIntoNewFileAsync(bundle, target, Ct));
        error.Code.ShouldBe(BundleError.Invalid);
        error.Message.ShouldContain("Transactions");
        File.Exists(target).ShouldBeFalse();
    }

    [Fact]
    public async Task Splits_that_do_not_add_up_are_refused_before_commit()
    {
        var bundle = await TamperedAsync(tables =>
        {
            var splits = Table(tables, nameof(TransactionSplit));
            var amount = Column(splits, nameof(TransactionSplit.Amount));
            var row = ((JsonArray)splits["rows"]!)[0]!;
            row[amount] = row[amount]!.GetValue<long>() + 1;
        });

        var error = await Should.ThrowAsync<BundleException>(() => Bundles.ImportIntoNewFileAsync(bundle, _temp.File("splits.keel"), Ct));
        error.Code.ShouldBe(BundleError.Invalid);
        error.Message.ShouldContain("split");
    }

    [Fact]
    public async Task A_bundle_is_never_imported_into_a_file_with_data()
    {
        var bundle = _temp.File("export.json");
        await Export.ExportBundleAsync(bundle, Ct);
        var before = await PortabilityKit.HashTablesAsync(_host.FilePath);

        (await Should.ThrowAsync<BundleException>(() => Bundles.ImportIntoNewFileAsync(bundle, _host.FilePath, Ct))).Code.ShouldBe(BundleError.TargetNotEmpty);
        (await PortabilityKit.HashTablesAsync(_host.FilePath)).ShouldBe(before);
        File.Exists(_host.FilePath).ShouldBeTrue("an existing file is never deleted");

        // An empty, migrated file is fine.
        var empty = _temp.File("Empty.keel");
        await using (var db = KeelDbContextFactory.CreateForFile(empty))
        {
            await db.Database.MigrateAsync();
        }

        (await Bundles.ImportIntoNewFileAsync(bundle, empty, Ct)).Count(nameof(Transaction)).ShouldBe(3_000);
    }

    private async Task<string> TamperedAsync(Action<JsonArray> change)
    {
        var original = _temp.File("original.json");
        await Export.ExportBundleAsync(original, Ct);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(original))!.AsObject();
        change((JsonArray)root["tables"]!);
        var path = _temp.File("tampered.json");
        await File.WriteAllTextAsync(path, root.ToJsonString());
        return path;
    }

    private static JsonObject Table(JsonArray tables, string entity) =>
        tables.Select(t => t!.AsObject()).Single(t => t["entity"]!.GetValue<string>() == entity);

    private static int Column(JsonObject table, string column) =>
        ((JsonArray)table["columns"]!).Select(c => c!.GetValue<string>()).ToList().IndexOf(column);
}

/// <summary>The 100k round trip (F-REP-6) with its timing line.</summary>
[Collection(nameof(TimingCollection))]
public sealed class BundleTimingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_100k_fixture_round_trips_table_by_table()
    {
        using var temp = new TempDirectory();
        await using var host = await LedgerTestHost.CreateAsync();
        var fixture = await LedgerFixtureGenerator.GenerateAsync(host.Factory, new LedgerFixtureOptions(), CancellationToken.None);
        await PortabilityKit.AddEverythingElseAsync(host.Factory, fixture, host.Get<IDataDirectory>().AttachmentsDirectoryFor(host.FilePath));

        var bundle = temp.File("export.json");
        var export = Stopwatch.StartNew();
        var info = await host.Get<IDataExportService>().ExportBundleAsync(bundle, CancellationToken.None);
        export.Stop();
        var target = temp.File("Restored.keel");
        var import = Stopwatch.StartNew();
        await host.Get<IBundleImportService>().ImportIntoNewFileAsync(bundle, target, CancellationToken.None);
        import.Stop();

        var csv = Stopwatch.StartNew();
        var csvResult = await host.Get<IDataExportService>().ExportCsvAsync(temp.File("csv"), asZip: false, CancellationToken.None);
        csv.Stop();

        output.WriteLine($"100k round trip: {info.Count(nameof(Transaction)):N0} transactions, {info.Counts.Values.Sum():N0} rows, bundle {new FileInfo(bundle).Length / 1024 / 1024} MB; export {export.ElapsedMilliseconds} ms, import {import.ElapsedMilliseconds} ms; CSV export {csv.ElapsedMilliseconds} ms ({csvResult.Rows[CsvExportFiles.Transactions]:N0} transaction lines)");
        var source = await PortabilityKit.HashTablesAsync(host.FilePath);
        var restored = await PortabilityKit.HashTablesAsync(target);
        foreach (var (table, hash) in source)
        {
            restored[table].ShouldBe(hash, $"table {table}");
        }

        source[nameof(Transaction)].Rows.ShouldBe(100_000);
    }
}
