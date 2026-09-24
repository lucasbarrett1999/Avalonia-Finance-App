using Keel.Domain.Budgeting;
using static Keel.Domain.Tests.Budgeting.BudgetBuilder;

namespace Keel.Domain.Tests.Budgeting;

public class BudgetMonthTests
{
    [Theory]
    [InlineData("2026-08-31", "2026-08-01")]
    [InlineData("2026-09-01", "2026-09-01")]
    [InlineData("2028-02-29", "2028-02-01")]
    [InlineData("2026-12-31", "2026-12-01")]
    [InlineData("0001-01-01", "0001-01-01")]
    public void Of_returns_the_first_day_of_the_month(string date, string expected) =>
        BudgetMonth.Of(D(date)).ShouldBe(D(expected));

    [Fact]
    public void Arithmetic_crosses_year_boundaries()
    {
        BudgetMonth.Add(D("2026-12-31"), 1).ShouldBe(D("2027-01-01"));
        BudgetMonth.Add(D("2026-01-31"), -1).ShouldBe(D("2025-12-01"));
        BudgetMonth.NextOf(D("2028-02-29")).ShouldBe(D("2028-03-01"));
        BudgetMonth.Between(D("2026-11-30"), D("2027-02-01")).ShouldBe(3);
        BudgetMonth.FromIndex(BudgetMonth.Index(D("2028-02-29"))).ShouldBe(D("2028-02-01"));
        BudgetMonth.Index(D("2027-01-01")).ShouldBe(BudgetMonth.Index(D("2026-12-01")) + 1);
    }
}
