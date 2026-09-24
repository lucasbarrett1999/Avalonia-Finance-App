using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Categorization;

public sealed class PayeeManagementTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task Lists_with_search_counts_and_default_categories()
    {
        var checking = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        await _host.AddAsync(checking.Id, -1, "Whole Foods");
        await _host.AddAsync(checking.Id, -1, "Whole Foods");
        await _host.AddAsync(checking.Id, -1, "Costco");
        var whole = (await _host.Payees.SearchAsync("whole", 1, Ct)).Single();
        await _host.Payees.SetDefaultCategoryAsync(whole.Id, groceries, Ct);

        var all = await _host.Payees.ListAsync(null, 100, Ct);
        all.Select(p => p.Name).ShouldContain("Costco");
        var item = (await _host.Payees.ListAsync("FOODS", 100, Ct)).ShouldHaveSingleItem();
        (item.Name, item.DefaultCategoryId, item.DefaultCategoryName, item.TransactionCount).ShouldBe(("Whole Foods", (Guid?)groceries, "Groceries", 2));

        _host.Undo.NextUndo.ShouldBe(LedgerAction.UpdatePayee);
        await _host.Undo.UndoAsync(Ct);
        (await _host.Payees.ListAsync("whole", 10, Ct)).Single().DefaultCategoryId.ShouldBeNull();
        await Should.ThrowAsync<LedgerValidationException>(() => _host.Payees.SetDefaultCategoryAsync(whole.Id, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Rename_applies_to_every_transaction_and_merges_into_an_existing_name()
    {
        var checking = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        var a = await _host.AddAsync(checking.Id, -1, "AMZN Mktp");
        var b = await _host.AddAsync(checking.Id, -2, "Amazon.com");
        var amzn = (await _host.Payees.SearchAsync("amzn", 1, Ct)).Single();
        await _host.Payees.SetDefaultCategoryAsync(amzn.Id, groceries, Ct);

        var renamed = await _host.Payees.RenameAsync(amzn.Id, "Amazon", Ct);
        renamed.Name.ShouldBe("Amazon");
        (await _host.Transactions.GetAsync(a.Id, Ct))!.Payee.ShouldBe("Amazon");

        var merged = await _host.Payees.RenameAsync((await _host.Payees.SearchAsync("amazon.com", 1, Ct)).Single().Id, "amazon", Ct);
        merged.Id.ShouldBe(amzn.Id);
        merged.DefaultCategoryId.ShouldBe(groceries);
        (await _host.Transactions.GetAsync(b.Id, Ct))!.Payee.ShouldBe("Amazon");
        await using (var db = _host.Db())
        {
            (await db.Payees.CountAsync(p => p.NormalizedName.StartsWith("AMAZON"))).ShouldBe(1);
        }

        await _host.Undo.UndoAsync(Ct);
        (await _host.Transactions.GetAsync(b.Id, Ct))!.Payee.ShouldBe("Amazon.com");
        await Should.ThrowAsync<LedgerValidationException>(() => _host.Payees.RenameAsync(amzn.Id, "  ", Ct));
    }
}
