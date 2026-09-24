using CsCheck;
using Keel.Domain.Import;

namespace Keel.Domain.Tests.Import;

public class JaroWinklerTests
{
    // Reference values from Winkler (1990) and the Wikipedia worked examples.
    [Theory]
    [InlineData("MARTHA", "MARHTA", 0.9444, 0.9611)]
    [InlineData("DWAYNE", "DUANE", 0.8222, 0.8400)]
    [InlineData("DIXON", "DICKSONX", 0.7667, 0.8133)]
    [InlineData("CRATE", "TRACE", 0.7333, 0.7333)]
    [InlineData("JONES", "JOHNSON", 0.7905, 0.8324)]
    public void Matches_reference_values(string a, string b, double jaro, double jaroWinkler)
    {
        JaroWinkler.Jaro(a, b).ShouldBe(jaro, 0.0001);
        JaroWinkler.Similarity(a, b).ShouldBe(jaroWinkler, 0.0001);
    }

    [Theory]
    [InlineData("", "", 1.0)]
    [InlineData("A", "", 0.0)]
    [InlineData("", "A", 0.0)]
    [InlineData("ABC", "XYZ", 0.0)]
    [InlineData("STARBUCKS", "STARBUCKS", 1.0)]
    [InlineData("A", "A", 1.0)]
    public void Handles_edge_cases(string a, string b, double expected)
    {
        JaroWinkler.Similarity(a, b).ShouldBe(expected, 1e-12);
    }

    [Fact]
    public void Prefix_bonus_needs_jaro_above_threshold()
    {
        // Jaro("AB", "AXYZWV") is 0.611 (below 0.7), so no prefix bonus even though "A" matches.
        var jaro = JaroWinkler.Jaro("AB", "AXYZWV");
        jaro.ShouldBeLessThan(0.7);
        JaroWinkler.Similarity("AB", "AXYZWV").ShouldBe(jaro);
    }

    [Theory]
    [InlineData("STARBUCKS", "STARBUCKS SEATTLE")]
    [InlineData("TRADER JOES", "TRADER JOE")]
    [InlineData("SHELL OIL", "SHELL")]
    public void Typical_payee_variants_pass_the_fuzzy_threshold(string a, string b)
    {
        JaroWinkler.Similarity(a, b).ShouldBeGreaterThanOrEqualTo(0.85);
    }

    [Theory]
    [InlineData("STARBUCKS", "SHELL OIL")]
    [InlineData("NETFLIX", "SPOTIFY")]
    public void Different_payees_fail_the_fuzzy_threshold(string a, string b)
    {
        JaroWinkler.Similarity(a, b).ShouldBeLessThan(0.85);
    }

    [Fact]
    public void Is_symmetric_and_bounded()
    {
        var word = Gen.Char["ABCDE "].Array[0, 12].Select(chars => new string(chars));
        Gen.Select(word, word)
            .Sample((a, b) =>
            {
                var ab = JaroWinkler.Similarity(a, b);
                var ba = JaroWinkler.Similarity(b, a);
                return Math.Abs(ab - ba) < 1e-12 && ab is >= 0 and <= 1 + 1e-12;
            });
    }
}
