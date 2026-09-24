using System.Text.Json.Nodes;
using Keel.Application.Import;
using Keel.Domain;
using Keel.Domain.Import;
using Keel.Infrastructure.Import;

namespace Keel.Infrastructure.Tests.Import;

/// <summary>
/// Fixture-driven importer tests (PRD 13): every file in <c>Import/Fixtures</c> is parsed with
/// default options and compared with its <c>.expected.json</c>. Run with
/// <c>KEEL_UPDATE_FIXTURES=1</c> to rewrite the expected files after a deliberate change, then
/// review the diff line by line.
/// </summary>
public class ImportFixtureTests
{
    private static readonly Guid Account = Guid.Parse("0190a1b2-0000-7000-8000-00000000f1f1");

    public static TheoryData<string> Fixtures() => new(FixtureNames());

    private static List<string> FixtureNames() =>
        Directory.GetFiles(FixtureJson.OutputDirectory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !name.EndsWith(".expected.json", StringComparison.Ordinal) && !name.StartsWith('.'))
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void Fixture_set_covers_the_required_layouts()
    {
        var names = FixtureNames();
        names.Count(n => n.EndsWith(".csv", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(5);
        names.Count(n => n.EndsWith(".ofx", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(2);
        names.Count(n => n.EndsWith(".qfx", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(1);
        names.Count(n => n.EndsWith(".qif", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(2);
        foreach (var name in names)
        {
            File.Exists(Path.Combine(FixtureJson.OutputDirectory, name + ".expected.json")).ShouldBeTrue(name);
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Fixture_parses_to_the_expected_result(string fixture)
    {
        var actual = FixtureJson.Project(await ParseAsync(fixture));

        var expectedPath = Path.Combine(FixtureJson.OutputDirectory, fixture + ".expected.json");
        if (Environment.GetEnvironmentVariable("KEEL_UPDATE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(Path.Combine(FixtureJson.SourceDirectory(), fixture + ".expected.json"), FixtureJson.Serialize(actual));
            return;
        }

        var expected = JsonNode.Parse(await File.ReadAllTextAsync(expectedPath))!;
        if (!JsonNode.DeepEquals(expected, actual))
        {
            FixtureJson.Serialize(actual).ShouldBe(FixtureJson.Serialize(expected), $"{fixture} differs from {fixture}.expected.json");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Importing_a_fixture_twice_inserts_nothing_the_second_time(string fixture)
    {
        var result = await ParseAsync(fixture);
        result.Transactions.ShouldNotBeEmpty();

        var first = DuplicateMatcher.Classify(Account, result.Transactions, []);
        first.ShouldAllBe(r => r.Decision == DedupDecision.Insert);

        var stored = first.Select((r, i) => new DedupCandidate(
            Guid.CreateVersion7(), Account, result.Transactions[i].Date, result.Transactions[i].Amount, r.NormalizedPayee,
            TransactionSource.File, result.Transactions[i].ProviderTransactionId, null, r.Fingerprint)).ToList();
        var second = DuplicateMatcher.Classify(Account, result.Transactions, stored);

        second.ShouldAllBe(r => r.Decision != DedupDecision.Insert);
        second.Where(r => result.Transactions[r.Index].ProviderTransactionId is not null)
            .ShouldAllBe(r => r.Decision == DedupDecision.UpdateByProviderId && !r.HasChanges);
        second.Where(r => result.Transactions[r.Index].ProviderTransactionId is null)
            .ShouldAllBe(r => r.Decision == DedupDecision.SkipExactFingerprint);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Csv_fixtures_parse_identically_with_their_detected_mapping(string fixture)
    {
        var auto = await ParseAsync(fixture);
        if (auto.CsvLayout is not { } layout)
        {
            return;
        }

        var explicitResult = await ParseAsync(fixture, ImportOptions.Default with { CsvMapping = layout.Mapping });

        FixtureJson.Serialize(new JsonArray(FixtureJson.Project(explicitResult)["transactions"]!.DeepClone()))
            .ShouldBe(FixtureJson.Serialize(new JsonArray(FixtureJson.Project(auto)["transactions"]!.DeepClone())));
    }

    internal static async Task<ParseResult> ParseAsync(string fixture, ImportOptions? options = null)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureJson.OutputDirectory, fixture));
        var parser = new FileImportParserResolver().Resolve(fixture, bytes.AsSpan(0, Math.Min(bytes.Length, 4096)));
        parser.ShouldNotBeNull(fixture);
        await using var stream = new MemoryStream(bytes);
        return await parser.ParseAsync(stream, options ?? ImportOptions.Default);
    }
}
