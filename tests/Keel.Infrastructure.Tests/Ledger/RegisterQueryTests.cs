using Keel.Application.Ledger;
using Keel.Domain;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Ledger;

public sealed class RegisterQueryTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;
    private LedgerFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _host = await LedgerTestHost.CreateAsync();
        _fixture = await LedgerFixtureGenerator.GenerateAsync(_host.Factory, new LedgerFixtureOptions(3_000, Seed: 7), CancellationToken.None);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    private IRegisterQuery Register => _host.Register;

    private Guid Visa => _fixture.Accounts["Visa Rewards"];

    private async Task<List<RegisterRow>> AllAsync(RegisterFilter filter, RegisterSort sort, int pageSize = 97)
    {
        var count = await Register.CountAsync(filter, Ct);
        var rows = new List<RegisterRow>();
        for (var skip = 0; skip < count; skip += pageSize)
        {
            rows.AddRange(await Register.GetPageAsync(filter, sort, skip, pageSize, Ct));
        }

        rows.Count.ShouldBe(count);
        return rows;
    }

    private async Task<IReadOnlyDictionary<Guid, long>> ExpectedBalancesAsync(Guid? accountId)
    {
        await using var db = _host.Db();
        var rows = await db.Transactions.Where(t => accountId == null || t.AccountId == accountId)
            .Select(t => new { t.Id, t.Date, t.Amount }).ToListAsync();
        return RunningBalance.Compute(rows.Select(r => (r.Id, r.Date, r.Amount)));
    }

    [Fact]
    public async Task Pages_follow_ledger_order_newest_first_with_correct_running_balances()
    {
        var rows = await AllAsync(new RegisterFilter(Visa), RegisterSort.Default);
        var expected = await ExpectedBalancesAsync(Visa);

        rows.Count.ShouldBe(expected.Count);
        for (var i = 1; i < rows.Count; i++)
        {
            RunningBalance.CompareLedgerOrder(rows[i - 1].Date, rows[i - 1].Id, rows[i].Date, rows[i].Id).ShouldBeGreaterThan(0);
        }

        rows.ShouldAllBe(r => r.RunningBalance == expected[r.Id]);
        rows[0].RunningBalance.ShouldBe((await _host.Accounts.GetAccountAsync(Visa, Ct))!.Balance.Amount);
    }

    [Fact]
    public async Task Running_balance_is_the_ledger_balance_under_any_sort_or_filter()
    {
        var expected = await ExpectedBalancesAsync(Visa);
        foreach (var column in Enum.GetValues<RegisterSortColumn>())
        {
            var sorted = await AllAsync(new RegisterFilter(Visa), new RegisterSort(column, Descending: false), pageSize: 200);
            sorted.ShouldAllBe(r => r.RunningBalance == expected[r.Id], $"sorted by {column}");
        }

        var filtered = await AllAsync(new RegisterFilter(Visa, Search: "payee:costco"), RegisterSort.Default);
        filtered.ShouldNotBeEmpty();
        filtered.ShouldAllBe(r => r.Payee == "Costco" && r.RunningBalance == expected[r.Id]);

        var all = await AllAsync(new RegisterFilter(), RegisterSort.Default, pageSize: 500);
        var expectedAll = await ExpectedBalancesAsync(null);
        all.Count.ShouldBe(_fixture.TransactionCount);
        all.ShouldAllBe(r => r.RunningBalance == expectedAll[r.Id]);
    }

    [Fact]
    public async Task Sorts_by_column()
    {
        var byAmount = await AllAsync(new RegisterFilter(Visa), new RegisterSort(RegisterSortColumn.Amount, Descending: false), 500);
        byAmount.Select(r => r.Amount).ShouldBeInOrder(SortDirection.Ascending);

        var byPayee = await AllAsync(new RegisterFilter(Visa), new RegisterSort(RegisterSortColumn.Payee, Descending: true), 500);
        var names = byPayee.Select(r => r.TransferAccountName ?? r.Payee).ToList();
        names.ShouldBe(names.OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase).ToList());

        var byAccount = await AllAsync(new RegisterFilter(Search: "date:2026-08"), new RegisterSort(RegisterSortColumn.Account, false), 500);
        byAccount.Select(r => r.AccountName).ShouldBe(byAccount.Select(r => r.AccountName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Filters_by_status_category_date_and_approval()
    {
        var cleared = await AllAsync(new RegisterFilter(Visa, Statuses: [TransactionStatus.Uncleared]), RegisterSort.Default);
        cleared.ShouldNotBeEmpty();
        cleared.ShouldAllBe(r => r.Status == TransactionStatus.Uncleared);

        var groceries = _fixture.Categories["Groceries"];
        var inGroceries = await AllAsync(new RegisterFilter(null, CategoryId: groceries), RegisterSort.Default, 500);
        inGroceries.ShouldAllBe(r => r.CategoryId == groceries || r.Splits.Any(s => s.CategoryId == groceries));
        inGroceries.ShouldContain(r => r.IsSplit, "split lines in the category count");

        var august = await AllAsync(new RegisterFilter(Visa, From: new DateOnly(2026, 8, 1), To: new DateOnly(2026, 8, 15)), RegisterSort.Default);
        august.ShouldAllBe(r => r.Date >= new DateOnly(2026, 8, 1) && r.Date <= new DateOnly(2026, 8, 15));

        var unapproved = await AllAsync(new RegisterFilter(UnapprovedOnly: true), RegisterSort.Default);
        unapproved.ShouldNotBeEmpty();
        unapproved.ShouldAllBe(r => !r.IsApproved);
        (await Register.GetSummaryAsync(null, Ct)).UnapprovedCount.ShouldBe(unapproved.Count);
    }

    [Fact]
    public async Task Search_syntax_matches_names_memos_amounts_and_dates()
    {
        var q = await AllAsync(new RegisterFilter(Search: "amount:>100 category:groceries date:2026-08"), RegisterSort.Default);
        q.ShouldNotBeEmpty();
        q.ShouldAllBe(r => Math.Abs(r.Amount) > 100_00 && r.Date.Month == 8 && r.Date.Year == 2026
            && (r.CategoryName == "Groceries" || r.Splits.Any(s => s.CategoryName == "Groceries")));

        var memo = await AllAsync(new RegisterFilter(Search: "memo:\"date night\""), RegisterSort.Default);
        memo.ShouldNotBeEmpty();
        memo.ShouldAllBe(r => r.Memo == "date night");

        var free = await AllAsync(new RegisterFilter(Search: "starbucks"), RegisterSort.Default);
        free.ShouldNotBeEmpty();
        free.ShouldAllBe(r => r.Payee == "Starbucks");

        var transfers = await AllAsync(new RegisterFilter(Search: "account:wallet payee:everyday"), RegisterSort.Default);
        transfers.ShouldNotBeEmpty();
        transfers.ShouldAllBe(r => r.AccountName == "Wallet" && r.TransferAccountName == "Everyday Checking");

        var exact = (await AllAsync(new RegisterFilter(Visa), RegisterSort.Default)).First(r => !r.IsTransfer);
        var byAmount = await AllAsync(new RegisterFilter(Visa, Search: (Math.Abs(exact.Amount) / 100m).ToString(System.Globalization.CultureInfo.InvariantCulture)), RegisterSort.Default);
        byAmount.ShouldContain(r => r.Id == exact.Id);

        (await Register.CountAsync(new RegisterFilter(Search: "100% _literal\\"), Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Index_of_finds_a_row_in_the_current_order()
    {
        var sort = new RegisterSort(RegisterSortColumn.Payee, Descending: false);
        var page = await Register.GetPageAsync(new RegisterFilter(Visa), sort, 250, 10, Ct);
        (await Register.IndexOfAsync(new RegisterFilter(Visa), sort, page[3].Id, Ct)).ShouldBe(253);
        (await Register.IndexOfAsync(new RegisterFilter(Visa, Statuses: [TransactionStatus.Reconciled]), sort, Guid.NewGuid(), Ct)).ShouldBe(-1);
    }

    [Fact]
    public async Task Summary_splits_ledger_into_cleared_and_uncleared()
    {
        var summary = await Register.GetSummaryAsync(Visa, Ct);
        var account = (await _host.Accounts.GetAccountAsync(Visa, Ct))!;
        summary.Ledger.ShouldBe(account.Balance.Amount);
        summary.Cleared.ShouldBe(account.ClearedBalance.Amount);
        summary.Uncleared.ShouldBe(account.UnclearedBalance.Amount);
        summary.Currency.ShouldBe("USD");
        summary.ReportedBalance.ShouldBeNull();
    }

    [Fact]
    public async Task Deleted_rows_disappear_and_balances_follow_edits()
    {
        var first = (await Register.GetPageAsync(new RegisterFilter(Visa), RegisterSort.Default, 0, 5, Ct)).ToList();
        var target = first[3];
        await _host.Transactions.DeleteAsync([target.Id], Ct);

        var after = await Register.GetPageAsync(new RegisterFilter(Visa), RegisterSort.Default, 0, 4, Ct);
        after.ShouldNotContain(r => r.Id == target.Id);
        after[0].RunningBalance.ShouldBe(first[0].RunningBalance - target.Amount);
    }
}
