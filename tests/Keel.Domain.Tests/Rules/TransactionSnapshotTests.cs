using Keel.Domain.Entities;
using Keel.Domain.Rules;

namespace Keel.Domain.Tests.Rules;

public class TransactionSnapshotTests
{
    [Fact]
    public void From_copies_a_ledger_transaction()
    {
        var transaction = new Transaction
        {
            AccountId = RuleTestData.Checking,
            Date = new DateOnly(2026, 9, 1),
            Amount = -1000,
            PayeeRaw = "COSTCO WHSE #1",
            Memo = "bulk",
            Source = TransactionSource.Provider,
            IsApproved = true,
        };
        transaction.Splits.Add(new TransactionSplit { CategoryId = RuleTestData.Groceries, Amount = -600 });
        transaction.Splits.Add(new TransactionSplit { CategoryId = RuleTestData.Household, Amount = -400, Memo = "soap" });

        var snapshot = TransactionSnapshot.From(transaction, "Costco", ["bulk"]);

        snapshot.Id.ShouldBe(transaction.Id);
        snapshot.PayeeRaw.ShouldBe("COSTCO WHSE #1");
        snapshot.Payee.ShouldBe("Costco");
        snapshot.Tags.ShouldBe(["bulk"]);
        snapshot.Source.ShouldBe(TransactionSource.Provider);
        snapshot.IsApproved.ShouldBeTrue();
        snapshot.Splits.ShouldBe([new SnapshotSplit(RuleTestData.Groceries, -600), new SnapshotSplit(RuleTestData.Household, -400, "soap")]);
        snapshot.NormalizedPayee().ShouldBe("COSTCO");
        TransactionSnapshot.From(transaction).Payee.ShouldBe("COSTCO WHSE #1");
    }

    [Fact]
    public void Effective_payee_falls_back_to_the_raw_descriptor()
    {
        (RuleTestData.Txn("SQ *BLUE BOTTLE") with { Payee = " " }).EffectivePayee.ShouldBe("SQ *BLUE BOTTLE");
    }

    [Fact]
    public void Equality_compares_lists_by_value()
    {
        var a = RuleTestData.Txn(tags: ["x"]) with { Splits = [new SnapshotSplit(null, -1)] };
        var b = RuleTestData.Txn(tags: ["x"]) with { Splits = [new SnapshotSplit(null, -1)] };
        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        (a with { Tags = ["y"] }).ShouldNotBe(b);
    }
}
