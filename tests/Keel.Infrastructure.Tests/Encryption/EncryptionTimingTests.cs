using System.Diagnostics;
using Keel.Application.Budget;
using Keel.Application.Files;
using Keel.Application.Ledger;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Keel.Infrastructure.Tests.Encryption;

/// <summary>
/// ADR 0101 numbers: the 100k-transaction fixture as a plain file and after encryption: file size, conversion
/// time, open time (key derivation included), register open and last page, and a budget month.
/// </summary>
[Collection(nameof(TimingCollection))]
public sealed class EncryptionTimingTests(ITestOutputHelper output)
{
    private const string Passphrase = "correct horse battery staple";

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task The_100k_fixture_stays_within_the_performance_budget_when_encrypted()
    {
        await using var host = new EncryptionTestHost();
        var session = await host.OpenAsync();
        await LedgerFixtureGenerator.GenerateAsync(session.GetRequiredService<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(), Ct);
        await host.CloseAllAsync();
        var plainSize = new FileInfo(host.FilePath).Length;

        var plain = await MeasureAsync(host, null);

        var convert = Stopwatch.StartNew();
        await host.ConvertAsync(new BudgetFileEncryptionChange(null, Passphrase));
        convert.Stop();
        var encryptedSize = new FileInfo(host.FilePath).Length;
        host.Restart();

        var encrypted = await MeasureAsync(host, new BudgetFileUnlock(Passphrase));

        output.WriteLine($"File size: plain {plainSize / 1024.0 / 1024.0:F1} MB, encrypted {encryptedSize / 1024.0 / 1024.0:F1} MB ({(encryptedSize - plainSize) * 100.0 / plainSize:+0.0;-0.0}%)");
        output.WriteLine($"Encrypt (backup + sqlcipher_export + verify + swap): {convert.ElapsedMilliseconds} ms");
        output.WriteLine($"Open file: plain {plain.Open} ms, encrypted {encrypted.Open} ms (key derivation included)");
        output.WriteLine($"Register open (count + summary + first page), best of 3: plain {plain.Register} ms, encrypted {encrypted.Register} ms");
        output.WriteLine($"Register last page: plain {plain.LastPage} ms, encrypted {encrypted.LastPage} ms");
        output.WriteLine($"Budget month (SQL aggregation + calculator), best of 3: plain {plain.Month} ms, encrypted {encrypted.Month} ms");

        encrypted.Register.ShouldBeLessThan(500); // PRD 11: register open < 500 ms at 100k rows
        encrypted.Month.ShouldBeLessThan(2_000);  // generous CI bound, like the plain month-switch test
    }

    private static async Task<(long Open, long Register, long LastPage, long Month)> MeasureAsync(EncryptionTestHost host, BudgetFileUnlock? unlock)
    {
        var open = Stopwatch.StartNew();
        var session = await host.OpenAsync(unlock);
        open.Stop();
        var register = session.GetRequiredService<IRegisterQuery>();
        var budget = session.GetRequiredService<IBudgetService>();
        var filter = new RegisterFilter();

        var best = long.MaxValue;
        for (var i = 0; i < 4; i++)
        {
            var watch = Stopwatch.StartNew();
            await register.CountAsync(filter, Ct);
            await register.GetSummaryAsync(null, Ct);
            await register.GetPageAsync(filter, RegisterSort.Default, 0, IRegisterQuery.PageSize, Ct);
            watch.Stop();
            if (i > 0)
            {
                best = Math.Min(best, watch.ElapsedMilliseconds);
            }
        }

        var deep = Stopwatch.StartNew();
        var total = await register.CountAsync(filter, Ct);
        (await register.GetPageAsync(filter, RegisterSort.Default, total - IRegisterQuery.PageSize, IRegisterQuery.PageSize, Ct)).Count.ShouldBe(IRegisterQuery.PageSize);
        deep.Stop();

        var month = long.MaxValue;
        var target = DateOnly.FromDateTime(DateTime.Today);
        target = new DateOnly(target.Year, target.Month, 1);
        await budget.GetMonthAsync(target, Ct);
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            await budget.GetMonthAsync(target.AddMonths(-i - 1), Ct);
            month = Math.Min(month, watch.ElapsedMilliseconds);
        }

        await host.CloseAllAsync();
        return (open.ElapsedMilliseconds, best, deep.ElapsedMilliseconds, month);
    }
}
