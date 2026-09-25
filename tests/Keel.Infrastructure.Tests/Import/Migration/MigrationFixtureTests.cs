using System.Text.Json;
using System.Text.Json.Nodes;
using Keel.Application.Import;
using Keel.Infrastructure.Import;
using Keel.Infrastructure.Import.Csv;
using Keel.Infrastructure.Import.Monarch;
using Keel.Infrastructure.Import.Ynab;

namespace Keel.Infrastructure.Tests.Import.Migration;

/// <summary>
/// The YNAB and Monarch export fixtures (<c>Import/Fixtures/Migration</c>, anonymized) parse to their reviewed
/// <c>.expected.json</c>. Regenerate with <c>KEEL_UPDATE_FIXTURES=1</c> after a deliberate parser change and
/// review the diff.
/// </summary>
public sealed class MigrationFixtureTests
{
    public static string Folder => Path.Combine(FixtureJson.OutputDirectory, "Migration");

    public static TheoryData<string> Fixtures() => new(
        Directory.GetFiles(Folder).Select(Path.GetFileName).OfType<string>()
            .Where(n => !n.EndsWith(".expected.json", StringComparison.Ordinal) && !n.StartsWith('.'))
            .Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Fixture_parses_to_the_expected_result(string fixture)
    {
        var actual = Project(await ParseAsync(fixture));
        if (Environment.GetEnvironmentVariable("KEEL_UPDATE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(Path.Combine(SourceFolder(), fixture + ".expected.json"), FixtureJson.Serialize(actual));
            return;
        }

        var expected = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Folder, fixture + ".expected.json")))!;
        if (!JsonNode.DeepEquals(expected, actual))
        {
            FixtureJson.Serialize(actual).ShouldBe(FixtureJson.Serialize(expected), $"{fixture} differs from {fixture}.expected.json");
        }
    }

    [Theory]
    [InlineData("ynab-register.csv", typeof(YnabRegisterParser), ImportFileFormat.Ynab)]
    [InlineData("ynab-budget.csv", typeof(YnabBudgetParser), ImportFileFormat.YnabBudget)]
    [InlineData("monarch-transactions.csv", typeof(MonarchImportParser), ImportFileFormat.Monarch)]
    public async Task Exports_are_recognized_by_header_ahead_of_the_generic_csv_detector(string fixture, Type parser, ImportFileFormat format)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(Folder, fixture));
        new FileImportParserResolver().Resolve(fixture, bytes).ShouldBeOfType(parser);
        new FileImportParserResolver().Resolve("renamed.txt", bytes).ShouldBeOfType(parser);
        (await ParseAsync(fixture)).Format.ShouldBe(format);
        (await ParseAsync(fixture)).IsMigration.ShouldBeTrue();
    }

    [Fact]
    public void An_ordinary_bank_csv_still_goes_to_the_csv_parser()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("Date,Description,Amount,Category\n2026-01-02,Grocer,-5.00,Food\n");
        new FileImportParserResolver().Resolve("bank.csv", bytes).ShouldBeOfType<CsvImportParser>();
    }

    [Fact]
    public async Task Ynab_register_reads_accounts_amounts_categories_and_status()
    {
        var result = await ParseAsync("ynab-register.csv");
        result.Warnings.ShouldBeEmpty();
        result.Accounts.Select(a => a.Name).ShouldBe(["Checking", "Savings", "Visa Card"]);
        result.Transactions.Count.ShouldBe(13);
        var payroll = result.Transactions.Single(t => t.PayeeRaw == "Acme Payroll");
        payroll.Amount.ShouldBe(1_850_00);
        payroll.Date.ShouldBe(new DateOnly(2026, 1, 16));
        payroll.Category.ShouldBe("Inflow: Ready to Assign");
        payroll.Extras["Cleared"].ShouldBe("Cleared");
        var power = result.Transactions.Single(t => t.PayeeRaw == "City Power");
        power.Amount.ShouldBe(-61_40);
        power.Extras["Flag"].ShouldBe("Red");
        power.Extras["Category Group"].ShouldBe("Bills");
        power.Extras["Category"].ShouldBe("Electric");
    }

    [Fact]
    public void Ynab_day_first_dates_are_detected_from_the_whole_column()
    {
        var csv = "\"Account\",\"Flag\",\"Date\",\"Payee\",\"Category Group/Category\",\"Category Group\",\"Category\",\"Memo\",\"Outflow\",\"Inflow\",\"Cleared\"\n"
            + "\"Current\",\"\",\"02/01/2026\",\"Shop\",\"\",\"\",\"\",\"\",\"4,50€\",\"0,00€\",\"Cleared\"\n"
            + "\"Current\",\"\",\"25/01/2026\",\"Shop\",\"\",\"\",\"\",\"\",\"1.204,50€\",\"0,00€\",\"Cleared\"\n";
        var result = YnabRegisterParser.Parse(System.Text.Encoding.UTF8.GetBytes(csv), ImportOptions.Default with { Currency = "EUR" });
        result.Transactions.Select(t => t.Date).ShouldBe([new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 25)]);
        result.Transactions.Select(t => t.Amount).ShouldBe([-4_50, -1_204_50]);
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ynab_budget_reads_months_and_assigned_amounts()
    {
        var result = await ParseAsync("ynab-budget.csv");
        result.Transactions.ShouldBeEmpty();
        result.BudgetRows.Count.ShouldBe(8);
        var groceries = result.BudgetRows.Where(r => r.Category == "Groceries").ToList();
        groceries.Select(r => r.Month).ShouldBe([new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1)]);
        groceries.Select(r => r.Assigned).ShouldBe([400_00, 450_00]);
        groceries[0].Activity.ShouldBe(-84_12);
        groceries[0].Available.ShouldBe(315_88);
    }

    [Fact]
    public async Task Monarch_reads_signed_amounts_merchants_and_tags()
    {
        var result = await ParseAsync("monarch-transactions.csv");
        result.Warnings.ShouldBeEmpty();
        result.Accounts.Select(a => a.Name).ShouldBe(["Joint Checking (...1234)", "Rewards Card (...9876)"]);
        var groceries = result.Transactions.Single(t => t.PayeeRaw == "Trader Joe's");
        groceries.Amount.ShouldBe(-86_45);
        MonarchImportParser.TagsOf(groceries).ShouldBe(["Household", "Weekly"]);
        groceries.Extras["Original Statement"].ShouldBe("TRADER JOE S #123 PORTLAND OR");
        result.Transactions.Single(t => t.Date == new DateOnly(2026, 2, 14)).PayeeRaw.ShouldBe("POS 4471 SQ *MYSTERY", "no merchant: the statement is the payee");
    }

    [Fact]
    public void Every_monarch_default_category_has_one_group_and_income_and_transfers_are_separate()
    {
        var all = MonarchCategories.Groups.SelectMany(g => g.Value).ToList();
        all.Count.ShouldBe(all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var (group, categories) in MonarchCategories.Groups)
        {
            foreach (var category in categories)
            {
                MonarchCategories.GroupOf(category).ShouldBe(group);
                MonarchCategories.GroupOf(category.ToUpperInvariant()).ShouldBe(group);
                MonarchCategories.Income.ShouldNotContain(category);
                MonarchCategories.Transfers.ShouldNotContain(category);
            }
        }

        MonarchCategories.GroupOf("Llama Grooming").ShouldBe(MonarchCategories.OtherGroup);
        MonarchCategories.Income.ShouldContain("Paychecks");
        MonarchCategories.Transfers.ShouldContain("Credit Card Payment");
    }

    private static string SourceFolder([System.Runtime.CompilerServices.CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "Fixtures", "Migration");

    internal static async Task<ParseResult> ParseAsync(string fixture, ImportOptions? options = null)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(Folder, fixture));
        var parser = new FileImportParserResolver().Resolve(fixture, bytes.AsSpan(0, Math.Min(bytes.Length, 4096))).ShouldNotBeNull();
        await using var stream = new MemoryStream(bytes);
        return await parser.ParseAsync(stream, options ?? ImportOptions.Default);
    }

    private static JsonObject Project(ParseResult result)
    {
        var root = FixtureJson.Project(result);
        if (result.BudgetRows.Count > 0)
        {
            root["budgetRows"] = JsonSerializer.SerializeToNode(result.BudgetRows, FixtureJson.Options);
        }

        return root;
    }
}
