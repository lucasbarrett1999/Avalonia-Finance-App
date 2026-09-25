using Keel.Domain.Ledger;

namespace Keel.Domain.Tests.Ledger;

public class TagNamesTests
{
    [Theory]
    [InlineData("  tax   2026 ", "tax 2026")]
    [InlineData("#receipts", "receipts")]
    [InlineData("  # vacation", "vacation")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Clean_trims_collapses_and_drops_a_leading_hash(string? input, string expected) =>
        TagNames.Clean(input).ShouldBe(expected);

    [Fact]
    public void Clean_cuts_to_the_column_length()
    {
        TagNames.Clean(new string('x', 150)).Length.ShouldBe(TagNames.MaxLength);
    }

    [Fact]
    public void Names_compare_case_insensitively_and_flagged_is_reserved()
    {
        TagNames.Same("Tax", " tax ").ShouldBeTrue();
        TagNames.Same("Tax", "Taxes").ShouldBeFalse();
        TagNames.IsReserved("flagged").ShouldBeTrue();
        TagNames.IsReserved("#Flagged").ShouldBeTrue();
        TagNames.IsReserved("Flag").ShouldBeFalse();
    }

    [Fact]
    public void Distinct_keeps_the_first_spelling_in_order()
    {
        TagNames.Distinct(["Tax", "", " tax", "Trip", null, "TRIP", "Work"]).ShouldBe(["Tax", "Trip", "Work"]);
    }
}
