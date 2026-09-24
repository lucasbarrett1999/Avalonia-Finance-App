using Keel.Domain.Ledger;

namespace Keel.Domain.Tests.Ledger;

public class SearchQueryTests
{
    [Fact]
    public void Parses_the_prd_example()
    {
        var q = SearchQuery.Parse("amount:>100 category:groceries date:2026-08 payee:\"trader joe\"");

        q.AmountMin.ShouldBe(100m);
        q.AmountMinInclusive.ShouldBeFalse();
        q.AmountMax.ShouldBeNull();
        q.Categories.ShouldBe(["groceries"]);
        q.DateFrom.ShouldBe(new DateOnly(2026, 8, 1));
        q.DateTo.ShouldBe(new DateOnly(2026, 8, 31));
        q.Payees.ShouldBe(["trader joe"]);
        q.Terms.ShouldBeEmpty();
        q.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Free_words_and_unknown_keys_are_terms()
    {
        var q = SearchQuery.Parse("coffee  foo:bar \"big box\"");
        q.Terms.ShouldBe(["coffee", "foo:bar", "big box"]);
        q.IsEmpty.ShouldBeFalse();
        SearchQuery.Parse("   ").IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData("amount:42.5", 42.5, true, 42.5, true)]
    [InlineData("amount:10..20", 10.0, true, 20.0, true)]
    [InlineData("amount:<=15", null, true, 15.0, true)]
    [InlineData("amount:<15", null, true, 15.0, false)]
    [InlineData("amount:>=-15", 15.0, true, null, true)]
    public void Parses_amount_forms(string text, double? min, bool minInclusive, double? max, bool maxInclusive)
    {
        var q = SearchQuery.Parse(text);
        q.AmountMin.ShouldBe(min is null ? null : (decimal)min);
        q.AmountMinInclusive.ShouldBe(minInclusive);
        q.AmountMax.ShouldBe(max is null ? null : (decimal)max);
        q.AmountMaxInclusive.ShouldBe(maxInclusive);
    }

    [Fact]
    public void Parses_date_forms()
    {
        var year = SearchQuery.Parse("date:2025");
        year.DateFrom.ShouldBe(new DateOnly(2025, 1, 1));
        year.DateTo.ShouldBe(new DateOnly(2025, 12, 31));

        var range = SearchQuery.Parse("date:2026-02..2026-03-15");
        range.DateFrom.ShouldBe(new DateOnly(2026, 2, 1));
        range.DateTo.ShouldBe(new DateOnly(2026, 3, 15));

        var after = SearchQuery.Parse("date:>2026-08");
        after.DateFrom.ShouldBe(new DateOnly(2026, 9, 1));
        after.DateTo.ShouldBeNull();

        var before = SearchQuery.Parse("date:<2026-08-10");
        before.DateTo.ShouldBe(new DateOnly(2026, 8, 9));
    }

    [Fact]
    public void Invalid_values_are_reported_and_ignored()
    {
        var q = SearchQuery.Parse("amount:lots date:2026-13 memo:rent");
        q.Errors.ShouldBe(["amount:lots", "date:2026-13"]);
        q.AmountMin.ShouldBeNull();
        q.DateFrom.ShouldBeNull();
        q.Memos.ShouldBe(["rent"]);
    }
}
