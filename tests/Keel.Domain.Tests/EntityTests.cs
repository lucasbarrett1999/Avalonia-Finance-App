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
    public void Entity_ids_are_time_ordered_version_7()
    {
        var first = EntityIds.New();
        var second = EntityIds.New();
        first.Version.ShouldBe(7);
        string.CompareOrdinal(first.ToString(), second.ToString()).ShouldBeLessThanOrEqualTo(0);
    }
}
