using System.Diagnostics;
using Keel.Application.Ledger;
using Keel.Infrastructure.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Keel.Infrastructure.Tests.Ledger;

/// <summary>
/// The review queue (M8 carried-over item): the Review screen counts unapproved rows and pages them
/// oldest first. With a large backlog in the 100k fixture both must stay fast, through the
/// <c>IX_Transactions_ReviewQueue</c> (IsApproved, Date, Id) index.
/// </summary>
[Collection(nameof(TimingCollection))]
public sealed class ReviewQueueTimingTests(ITestOutputHelper output)
{
    private static readonly RegisterFilter Unapproved = new(UnapprovedOnly: true);
    private static readonly RegisterSort OldestFirst = new(RegisterSortColumn.Date, Descending: false);

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task Review_queue_count_and_pages_over_100k_rows_are_fast_and_use_the_index()
    {
        await using var host = await LedgerTestHost.CreateAsync();
        var fixture = await LedgerFixtureGenerator.GenerateAsync(host.Factory, new LedgerFixtureOptions(), Ct);
        fixture.TransactionCount.ShouldBe(100_000);

        // A realistic worst case: a fifth of the ledger waits for review (e.g. a year of imports).
        await using (var db = host.Db())
        {
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "Transactions" SET "IsApproved" = 0 WHERE "Source" <> 'System' AND substr("Id", -1) IN ('0', '4', '8')""");
            await db.Database.ExecuteSqlRawAsync("ANALYZE");
        }

        var expected = await host.Register.CountAsync(Unapproved, Ct);
        expected.ShouldBeGreaterThan(10_000);

        long bestCount = long.MaxValue, bestFirst = long.MaxValue, bestDeep = long.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            (await host.Register.CountAsync(Unapproved, Ct)).ShouldBe(expected);
            bestCount = Math.Min(bestCount, watch.ElapsedMilliseconds);

            watch.Restart();
            var first = await host.Register.GetPageAsync(Unapproved, OldestFirst, 0, IRegisterQuery.PageSize, Ct);
            bestFirst = Math.Min(bestFirst, watch.ElapsedMilliseconds);
            first.Count.ShouldBe(IRegisterQuery.PageSize);
            first.ShouldAllBe(r => !r.IsApproved);
            first.Select(r => r.Date).ShouldBeInOrder(SortDirection.Ascending);

            watch.Restart();
            var deep = await host.Register.GetPageAsync(Unapproved, OldestFirst, expected - IRegisterQuery.PageSize, IRegisterQuery.PageSize, Ct);
            bestDeep = Math.Min(bestDeep, watch.ElapsedMilliseconds);
            deep.Count.ShouldBe(IRegisterQuery.PageSize);
        }

        output.WriteLine($"Review queue of {expected:N0} unapproved rows: count {bestCount} ms, first page {bestFirst} ms, last page {bestDeep} ms (best of 3)");
        bestCount.ShouldBeLessThan(250);
        bestFirst.ShouldBeLessThan(250);
        bestDeep.ShouldBeLessThan(500);

        // The planner picks the review-queue index for the unapproved filter.
        await using var check = host.Db();
        var connection = check.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """EXPLAIN QUERY PLAN SELECT "Id" FROM "Transactions" WHERE "IsDeleted" = 0 AND "IsApproved" = 0 ORDER BY "Date", "Id" LIMIT 200""";
        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(3));
            }
        }

        output.WriteLine(string.Join(Environment.NewLine, plan));
        plan.ShouldContain(p => p.Contains("IX_Transactions_ReviewQueue", StringComparison.Ordinal));
    }
}
