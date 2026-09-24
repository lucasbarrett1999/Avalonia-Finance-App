using Keel.Domain;

namespace Keel.Infrastructure.Tests.Ledger;

public sealed class PayeeAndSnapshotTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task Get_or_create_is_keyed_by_normalized_name()
    {
        var first = await _host.Payees.GetOrCreateAsync("Blue  Bottle", Ct);
        var second = await _host.Payees.GetOrCreateAsync(" blue bottle ", Ct);
        second.Id.ShouldBe(first.Id);
        second.Name.ShouldBe("Blue Bottle");
    }

    [Fact]
    public async Task Autocomplete_prefers_prefix_matches_then_usage()
    {
        var checking = await _host.CheckingAsync(opening: 0);
        await _host.AddAsync(checking.Id, -1, "Coffee Bar");
        await _host.AddAsync(checking.Id, -1, "Costco");
        await _host.AddAsync(checking.Id, -1, "Costco");
        await _host.AddAsync(checking.Id, -1, "Blue Coffee");

        var results = await _host.Payees.SearchAsync("co", 10, Ct);
        results.Select(p => p.Name).ShouldBe(["Costco", "Coffee Bar", "Blue Coffee"]);
        (await _host.Payees.SearchAsync("co", 1, Ct)).ShouldHaveSingleItem().Name.ShouldBe("Costco");
    }

    [Fact]
    public async Task Suggestion_uses_the_last_category_and_memo()
    {
        var checking = await _host.CheckingAsync(opening: 0);
        var card = await _host.AccountAsync("Card", AccountType.CreditCard);
        var groceries = await _host.CategoryAsync("Groceries");
        var dining = await _host.CategoryAsync("Dining");
        await _host.AddAsync(checking.Id, -1, "Market", groceries, new DateOnly(2026, 8, 1), "weekly");
        await _host.AddAsync(card.Id, -1, "Market", dining, new DateOnly(2026, 8, 5), "lunch");

        var any = (await _host.Payees.GetSuggestionAsync("market", null, Ct))!;
        any.CategoryId.ShouldBe(dining);
        any.CategoryName.ShouldBe("Dining");
        any.Memo.ShouldBe("lunch");

        var inChecking = (await _host.Payees.GetSuggestionAsync("MARKET", checking.Id, Ct))!;
        inChecking.CategoryId.ShouldBe(groceries);
        inChecking.Memo.ShouldBe("weekly");

        (await _host.Payees.GetSuggestionAsync("unknown", null, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Snapshots_upsert_and_resolve_the_latest_on_or_before_a_date()
    {
        var brokerage = await _host.AccountAsync("Brokerage", AccountType.Investment);
        await _host.Snapshots.RecordAsync(brokerage.Id, new DateOnly(2026, 6, 30), 1_000_000, BalanceSource.Manual, Ct);
        await _host.Snapshots.RecordAsync(brokerage.Id, new DateOnly(2026, 7, 31), 1_050_000, BalanceSource.Manual, Ct);
        await _host.Snapshots.RecordAsync(brokerage.Id, new DateOnly(2026, 7, 31), 1_060_000, BalanceSource.Provider, Ct);

        var all = await _host.Snapshots.GetSnapshotsAsync(brokerage.Id, Ct);
        all.Select(s => s.Balance).ShouldBe([1_060_000, 1_000_000]);
        all[0].Source.ShouldBe(BalanceSource.Provider);

        var asOf = await _host.Snapshots.GetLatestOnOrBeforeAsync([brokerage.Id], new DateOnly(2026, 7, 15), Ct);
        asOf[brokerage.Id].Balance.ShouldBe(1_000_000);
        (await _host.Snapshots.GetLatestOnOrBeforeAsync([brokerage.Id], new DateOnly(2026, 1, 1), Ct)).ShouldBeEmpty();

        await _host.Snapshots.DeleteAsync(brokerage.Id, new DateOnly(2026, 7, 31), Ct);
        (await _host.Snapshots.GetSnapshotsAsync(brokerage.Id, Ct)).ShouldHaveSingleItem();
        await _host.Undo.UndoAsync(Ct);
        (await _host.Snapshots.GetSnapshotsAsync(brokerage.Id, Ct)).Count.ShouldBe(2);
        _host.Bus.LedgerChanges.Last().AccountIds.ShouldBe([brokerage.Id]);
    }
}
