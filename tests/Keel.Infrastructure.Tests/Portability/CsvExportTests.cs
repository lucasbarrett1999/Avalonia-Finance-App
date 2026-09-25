using System.Globalization;
using System.IO.Compression;
using System.Text;
using CsvHelper;
using Keel.Application.Ledger;
using Keel.Application.Portability;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;
using Target = Keel.Domain.Entities.Target;

namespace Keel.Infrastructure.Tests.Portability;

/// <summary>F-REP-6 CSV export: files, stable columns, ISO dates, decimal amounts, flattened splits, paging.</summary>
public sealed class CsvExportTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private LedgerTestHost _host = null!;

    private static CancellationToken Ct => CancellationToken.None;

    private IDataExportService Export => _host.Get<IDataExportService>();

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _temp.Dispose();
    }

    [Fact]
    public async Task Every_file_has_its_stable_header_iso_dates_and_decimal_amounts()
    {
        var ids = await BuildLedgerAsync();
        var folder = _temp.File("export");
        var result = await Export.ExportCsvAsync(folder, asZip: false, Ct);

        result.Rows.Keys.Order().ShouldBe(CsvExportFiles.All.Order());
        foreach (var name in CsvExportFiles.All)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(folder, name));
            bytes.Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF }, $"{name} has no byte-order mark");
        }

        Header(folder, CsvExportFiles.Transactions).ShouldBe("Id,Parent Id,Date,Account,Payee,Category Group,Category,Transfer Account,Memo,Amount,Currency,Status,Approved,Source,Tags,Import Id");
        Header(folder, CsvExportFiles.Budget).ShouldBe("Month,Category Group,Category,Category Id,Assigned,Currency");
        Header(folder, CsvExportFiles.Accounts).ShouldBe("Id,Name,Type,On Budget,Closed,Currency,Opening Date,Balance,Cleared Balance,Sort Order,Notes");
        Header(folder, CsvExportFiles.Categories).ShouldBe("Group Id,Group,Group Sort Order,Group Hidden,Group System,Id,Category,Sort Order,Hidden,System,Linked Account,Flex Kind,Notes");
        Header(folder, CsvExportFiles.Payees).ShouldBe("Id,Name,Default Category Group,Default Category,Transfer Payee For");
        Header(folder, CsvExportFiles.Rules).ShouldBe("Id,Sort Order,Name,Enabled,Continue After Match,Conditions,Actions");
        Header(folder, CsvExportFiles.Targets).ShouldBe("Category Id,Category Group,Category,Type,Amount,Currency,Target Date,Cadence,Linked Account");
        Header(folder, CsvExportFiles.Scheduled).ShouldBe("Id,Account,Payee,Category Group,Category,Transfer Account,Memo,Amount,Currency,Recurrence Rule,Next Date,End Date,Auto Enter");
        Header(folder, CsvExportFiles.Recurring).ShouldBe("Id,Payee,Account,Cadence,Expected Amount,Amount Tolerance,Currency,Variable Amount,Next Expected Date,Last Seen Date,Confidence,Status,Subscription,Category Group,Category,Scheduled Transaction Id");

        var transactions = Read(folder, CsvExportFiles.Transactions);
        transactions.Count.ShouldBe(result.Rows[CsvExportFiles.Transactions]);
        transactions.Count.ShouldBe(7, "opening balance, grocery, two split lines, two transfer sides, imported coffee; the deleted row is left out");
        var grocery = transactions.Single(r => r["Payee"] == "Corner Grocer");
        grocery["Date"].ShouldBe("2026-08-10");
        grocery["Amount"].ShouldBe("-12.30");
        grocery["Currency"].ShouldBe("USD");
        grocery["Category Group"].ShouldBe("Everyday");
        grocery["Category"].ShouldBe("Groceries");
        grocery["Memo"].ShouldBe("weekly, \"big\" shop");
        grocery["Status"].ShouldBe("Cleared");
        grocery["Approved"].ShouldBe("true");
        grocery["Tags"].ShouldBe("Reimbursable, Vacation");
        grocery["Id"].ShouldBe(ids.Grocery.ToString("D"));
        grocery["Parent Id"].ShouldBeEmpty();

        var split = transactions.Where(r => r["Parent Id"] == ids.Split.ToString("D")).ToList();
        split.Select(r => r["Amount"]).ShouldBe(["-60.00", "-40.00"], ignoreOrder: true);
        split.Select(r => r["Category"]).ShouldBe(["Groceries", "Household"], ignoreOrder: true);
        split.ShouldAllBe(r => r["Payee"] == "Big Box" && r["Date"] == "2026-08-12");

        var transfer = transactions.Where(r => r["Transfer Account"].Length > 0).ToList();
        transfer.Select(r => (r["Account"], r["Transfer Account"], r["Amount"])).ShouldBe([("Checking", "Savings", "-250.00"), ("Savings", "Checking", "250.00")], ignoreOrder: true);
        transactions.Single(r => r["Import Id"] == "FITID-42")["Source"].ShouldBe("File");
        transactions.Select(r => r["Date"]).ShouldBe(transactions.Select(r => r["Date"]).Order(StringComparer.Ordinal).ToList(), "ledger order");

        var budget = Read(folder, CsvExportFiles.Budget).ShouldHaveSingleItem();
        (budget["Month"], budget["Category"], budget["Assigned"]).ShouldBe(("2026-08", "Groceries", "450.00"));
        var checking = Read(folder, CsvExportFiles.Accounts).Single(r => r["Name"] == "Checking");
        (checking["Type"], checking["On Budget"], checking["Opening Date"], checking["Balance"]).ShouldBe(("Checking", "true", "2026-08-01", "632.95"), "1000.00 - 12.30 - 100.00 - 250.00 - 4.75");
        Read(folder, CsvExportFiles.Categories).ShouldContain(r => r["Group"] == "Inflow" && r["Category"] == "Ready to Assign" && r["System"] == "true");
        var rule = Read(folder, CsvExportFiles.Rules).ShouldHaveSingleItem();
        rule["Conditions"].ShouldBe("""{"v":1,"all":[]}""");
        var target = Read(folder, CsvExportFiles.Targets).ShouldHaveSingleItem();
        (target["Type"], target["Amount"], target["Target Date"]).ShouldBe(("SavingsBalanceByDate", "1200.00", "2027-01-01"));
        var schedule = Read(folder, CsvExportFiles.Scheduled).ShouldHaveSingleItem();
        (schedule["Amount"], schedule["Next Date"], schedule["Auto Enter"], schedule["Payee"]).ShouldBe(("-9.99", "2026-09-01", "true", "Corner Grocer"));
        var recurring = Read(folder, CsvExportFiles.Recurring).ShouldHaveSingleItem();
        (recurring["Cadence"], recurring["Expected Amount"], recurring["Confidence"], recurring["Status"]).ShouldBe(("Monthly", "-9.99", "0.875", "Active"));
    }

    [Fact]
    public async Task The_zip_holds_the_same_files_as_the_folder()
    {
        await BuildLedgerAsync();
        var folder = _temp.File("folder");
        var zip = _temp.File("export.zip");
        await Export.ExportCsvAsync(folder, asZip: false, Ct);
        var result = await Export.ExportCsvAsync(zip, asZip: true, Ct);
        result.Path.ShouldBe(zip);
        File.Exists(zip + ".partial").ShouldBeFalse();

        using var archive = ZipFile.OpenRead(zip);
        archive.Entries.Select(e => e.FullName).ShouldBe(CsvExportFiles.All);
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            (await reader.ReadToEndAsync()).ShouldBe(await File.ReadAllTextAsync(Path.Combine(folder, entry.FullName)), entry.FullName);
        }
    }

    [Fact]
    public async Task A_folder_that_already_holds_an_export_is_refused()
    {
        await BuildLedgerAsync();
        var folder = _temp.File("again");
        await Export.ExportCsvAsync(folder, asZip: false, Ct);
        await Should.ThrowAsync<IOException>(() => Export.ExportCsvAsync(folder, asZip: false, Ct));
    }

    [Fact]
    public async Task Transactions_are_written_page_by_page_in_ledger_order()
    {
        var fixture = await LedgerFixtureGenerator.GenerateAsync(_host.Factory, new LedgerFixtureOptions(DataExportServicePages * 2 + 321, Seed: 3), Ct);
        var folder = _temp.File("paged");
        var result = await Export.ExportCsvAsync(folder, asZip: false, Ct);

        await using var db = _host.Db();
        var splitParents = await db.TransactionSplits.Select(s => s.TransactionId).Distinct().CountAsync();
        var expected = fixture.TransactionCount - splitParents + fixture.SplitCount;
        result.Rows[CsvExportFiles.Transactions].ShouldBe(expected);
        var rows = Read(folder, CsvExportFiles.Transactions);
        rows.Count.ShouldBe(expected);
        rows.Select(r => r["Id"]).Distinct().Count().ShouldBe(expected);
        var keys = rows.Select(r => r["Date"] + "|" + (r["Parent Id"].Length > 0 ? r["Parent Id"] : r["Id"]).ToUpperInvariant()).ToList();
        keys.ShouldBe(keys.Order(StringComparer.Ordinal).ToList());
    }

    private const int DataExportServicePages = Keel.Infrastructure.Portability.DataExportService.PageSize;

    private static string Header(string folder, string file) => File.ReadLines(Path.Combine(folder, file)).First();

    private static List<Dictionary<string, string>> Read(string folder, string file)
    {
        using var reader = new StreamReader(Path.Combine(folder, file));
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        csv.Read();
        csv.ReadHeader();
        var header = csv.HeaderRecord!;
        var rows = new List<Dictionary<string, string>>();
        while (csv.Read())
        {
            rows.Add(header.ToDictionary(h => h, h => csv.GetField(h) ?? string.Empty));
        }

        return rows;
    }

    private async Task<(Guid Grocery, Guid Split)> BuildLedgerAsync()
    {
        var checking = await _host.CheckingAsync("Checking", 1_000_00);
        var savings = await _host.AccountAsync("Savings", AccountType.Savings);
        var groceries = await _host.CategoryAsync("Groceries");
        var household = await _host.CategoryAsync("Household");
        var grocery = await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 10), -12_30, "Corner Grocer", groceries, "weekly, \"big\" shop", TransactionStatus.Cleared), Ct);
        var split = await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 12), -100_00, "Big Box", null, null,
            Splits: [new SplitLine(groceries, null, -60_00), new SplitLine(household, null, -40_00)]), Ct);
        await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 14), -250_00, null, null, "to savings", TransferAccountId: savings.Id), Ct);
        var deleted = await _host.AddAsync(checking.Id, -1_00, "Oops", date: new DateOnly(2026, 8, 15));
        await _host.Transactions.DeleteAsync([deleted.Id], Ct);
        await _host.Get<Keel.Application.Import.IImportService>().ImportTransactionsAsync(TransactionSource.File,
            new Keel.Application.Import.ImportBatch(checking.Id, [new Keel.Application.Import.IncomingTransaction(new DateOnly(2026, 8, 16), -4_75, "Bean There", ProviderTransactionId: "FITID-42")]), Ct);

        await using var db = _host.Db();
        var tags = new[] { new Tag { Name = "Vacation" }, new Tag { Name = "Reimbursable" } };
        db.Tags.AddRange(tags);
        db.TransactionTags.AddRange(tags.Select(t => new TransactionTag { TransactionId = grocery.Id, TagId = t.Id }));
        db.BudgetAssignments.Add(new BudgetAssignment { CategoryId = groceries, Month = new DateOnly(2026, 8, 1), Assigned = 450_00 });
        db.Rules.Add(new Rule { Name = "Coffee", ConditionsJson = """{"v":1,"all":[]}""", ActionsJson = """{"v":1,"actions":[]}""" });
        db.Targets.Add(new Target { CategoryId = household, Type = TargetType.SavingsBalanceByDate, Amount = 1_200_00, TargetDate = new DateOnly(2027, 1, 1) });
        var payee = await db.Payees.SingleAsync(p => p.Name == "Corner Grocer");
        var schedule = new ScheduledTransaction { AccountId = checking.Id, Amount = -9_99, PayeeId = payee.Id, RecurrenceRule = "DTSTART:20260901\nRRULE:FREQ=MONTHLY", NextDate = new DateOnly(2026, 9, 1), AutoEnter = true };
        db.ScheduledTransactions.Add(schedule);
        db.RecurringItems.Add(new RecurringItem
        {
            PayeeId = payee.Id,
            AccountId = checking.Id,
            Cadence = RecurrenceCadence.Monthly,
            ExpectedAmount = -9_99,
            AmountTolerance = 2_00,
            NextExpectedDate = new DateOnly(2026, 9, 1),
            LastSeenDate = new DateOnly(2026, 8, 1),
            Confidence = 0.875,
            Status = RecurringStatus.Active,
            ScheduledTransactionId = schedule.Id,
        });
        await db.SaveChangesAsync();
        return (grocery.Id, split.Id);
    }
}
