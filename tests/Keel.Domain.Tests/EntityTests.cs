using Keel.Domain.Entities;

namespace Keel.Domain.Tests;

public class EntityTests
{
    [Fact]
    public void Split_balance_is_checked_in_memory()
    {
        var txn = new Transaction { Amount = -10_000 };
        txn.SplitsBalance.ShouldBeTrue();

        txn.Splits.Add(new TransactionSplit { Amount = -6_000 });
        txn.SplitsBalance.ShouldBeFalse();

        txn.Splits.Add(new TransactionSplit { Amount = -4_000 });
        txn.IsSplit.ShouldBeTrue();
        txn.SplitsBalance.ShouldBeTrue();
    }

    [Fact]
    public void Month_of_returns_first_day()
    {
        BudgetAssignment.MonthOf(new DateOnly(2026, 8, 31)).ShouldBe(new DateOnly(2026, 8, 1));
    }

    [Fact]
    public void Entity_ids_are_unique_version_7_guids()
    {
        var ids = Enumerable.Range(0, 1_000).Select(_ => EntityIds.New()).ToList();
        ids.ShouldAllBe(id => id.Version == 7);
        ids.Distinct().Count().ShouldBe(ids.Count);
    }
}
