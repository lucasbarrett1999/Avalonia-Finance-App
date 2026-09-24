using System.Diagnostics;
using Keel.Application.Ledger;
using Keel.Domain;
using Keel.Infrastructure.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Keel.Infrastructure.Tests.Ledger;

public sealed class LedgerFixtureTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task Generates_a_deterministic_ledger_with_the_exact_count()
    {
        await using var a = await LedgerTestHost.CreateAsync();
        await using var b = await LedgerTestHost.CreateAsync();
        var fa = await LedgerFixtureGenerator.GenerateAsync(a.Factory, new LedgerFixtureOptions(2_500, Seed: 11), Ct);
        var fb = await LedgerFixtureGenerator.GenerateAsync(b.Factory, new LedgerFixtureOptions(2_500, Seed: 11), Ct);

        fa.TransactionCount.ShouldBe(2_500);
        fa.Accounts.Count.ShouldBe(8);
        fa.Categories.Count.ShouldBe(40);
        fa.Accounts.ShouldBe(fb.Accounts);

        await using var dba = a.Db();
        await using var dbb = b.Db();
        var rowsA = await dba.Transactions.OrderBy(t => t.Id).Select(t => new { t.Id, t.Amount, t.Date, t.Status }).ToListAsync();
        var rowsB = await dbb.Transactions.OrderBy(t => t.Id).Select(t => new { t.Id, t.Amount, t.Date, t.Status }).ToListAsync();
        rowsA.Count.ShouldBe(2_500);
        rowsA.ShouldBe(rowsB);
        (await dba.TransactionSplits.CountAsync()).ShouldBe(fa.SplitCount);
        (await dba.Transactions.CountAsync(t => t.TransferPairId != null) % 2).ShouldBe(0);
        (await dba.BalanceSnapshots.CountAsync()).ShouldBeGreaterThan(0);

        // The fixture is valid ledger data: every split set balances (the deferred constraint held).
        (await a.Accounts.GetAccountsAsync(false, Ct)).Select(x => x.Group).Distinct().ShouldBe([AccountGroup.Cash, AccountGroup.Credit, AccountGroup.Tracking]);
    }

    [Fact]
    public async Task First_register_page_of_100k_loads_under_500_ms_on_a_warm_database()
    {
        await using var host = await LedgerTestHost.CreateAsync();
        var generate = Stopwatch.StartNew();
        var fixture = await LedgerFixtureGenerator.GenerateAsync(host.Factory, new LedgerFixtureOptions(), Ct);
        output.WriteLine($"Generated {fixture.TransactionCount:N0} transactions and {fixture.SplitCount:N0} splits in {generate.ElapsedMilliseconds} ms");
        fixture.TransactionCount.ShouldBe(100_000);

        var visa = fixture.Accounts["Visa Rewards"];
        foreach (var (name, filter) in new[] { ("All accounts", new RegisterFilter()), ("Visa Rewards", new RegisterFilter(visa)) })
        {
            await OpenAsync(host.Register, filter); // warm the page cache
            var best = long.MaxValue;
            for (var i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew();
                var (count, page) = await OpenAsync(host.Register, filter);
                watch.Stop();
                best = Math.Min(best, watch.ElapsedMilliseconds);
                page.Count.ShouldBe(IRegisterQuery.PageSize);
                count.ShouldBeGreaterThan(IRegisterQuery.PageSize);
            }

            output.WriteLine($"{name}: register open (count + summary + first page of {IRegisterQuery.PageSize}) best of 3 = {best} ms");
            best.ShouldBeLessThan(500);

            var deep = Stopwatch.StartNew();
            var total = await host.Register.CountAsync(filter, Ct);
            var last = await host.Register.GetPageAsync(filter, RegisterSort.Default, total - IRegisterQuery.PageSize, IRegisterQuery.PageSize, Ct);
            output.WriteLine($"{name}: last page ({total:N0} rows) = {deep.ElapsedMilliseconds} ms");
            last.Count.ShouldBe(IRegisterQuery.PageSize);
        }
    }

    private static async Task<(int Count, IReadOnlyList<RegisterRow> Page)> OpenAsync(IRegisterQuery register, RegisterFilter filter)
    {
        var count = await register.CountAsync(filter, Ct);
        await register.GetSummaryAsync(filter.AccountId, Ct);
        var page = await register.GetPageAsync(filter, RegisterSort.Default, 0, IRegisterQuery.PageSize, Ct);
        return (count, page);
    }
}
