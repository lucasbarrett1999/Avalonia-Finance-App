using Keel.Application.Categorization;
using Keel.Application.Ledger;
using Keel.Domain.Categorization;
using Keel.Domain.Entities;
using Keel.Infrastructure.Categorization;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Categorization;

public sealed class LearnerServiceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    private LearnerService Learner => (LearnerService)_host.Get<ILearnerService>();

    // A fresh service over the same file: what the next app session sees.
    private LearnerService NextSession() => new(_host.Factory, _host.Get<Keel.Application.Files.IBudgetFileService>());

    private async Task<string> CachedJsonAsync()
    {
        await using var db = _host.Db();
        return (await db.Settings.SingleAsync(s => s.Key == LearnerService.SettingKey)).ValueJson;
    }

    private async Task<(Guid Checking, Guid Groceries, Guid Dining)> HistoryAsync()
    {
        var checking = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        var dining = await _host.CategoryAsync("Dining");
        for (var i = 0; i < 4; i++)
        {
            await _host.AddAsync(checking.Id, -2_000 - i, "TRADER JOES", groceries, new DateOnly(2026, 8, 2 + i));
            await _host.AddAsync(checking.Id, -1_500 - i, "Taco Truck", dining, new DateOnly(2026, 8, 2 + i));
        }

        return (checking.Id, groceries, dining);
    }

    [Fact]
    public async Task Builds_from_approved_history_on_first_use_and_caches_with_a_version_key()
    {
        var (checking, groceries, _) = await HistoryAsync();
        await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, checking, new DateOnly(2026, 8, 20), -900, "TRADER JOES", groceries, null, IsApproved: false), Ct);
        Learner.Status.ShouldBe(LearnerStatus.NotLoaded);

        var model = await Learner.GetModelAsync(Ct);
        Learner.Status.ShouldBe(LearnerStatus.Ready);
        Learner.LastLoad.ShouldBe(LearnerLoadKind.Built);
        model.ExampleCount.ShouldBe(8, "unapproved rows and the system starting balance are not examples");
        model.PayeeHistory("TRADER JOES")[groceries].ShouldBe(4);

        var cache = await CachedJsonAsync();
        cache.ShouldContain($"\"versionKey\":\"{LearnerService.VersionKey}\"");
        cache.ShouldContain("\"format\":\"keel.categoryLearner\"");

        var next = NextSession();
        (await next.GetModelAsync(Ct)).ToJson().ShouldBe(model.ToJson());
        next.LastLoad.ShouldBe(LearnerLoadKind.Cache);
    }

    [Fact]
    public async Task Approvals_category_changes_edits_deletes_and_undo_update_the_model_incrementally()
    {
        var (checking, groceries, dining) = await HistoryAsync();
        var pending = await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, checking, new DateOnly(2026, 8, 20), -900, "TRADER JOES", groceries, null, IsApproved: false), Ct);
        var first = await Learner.GetModelAsync(Ct);
        first.ExampleCount.ShouldBe(8);

        async Task ShouldMatchRetrainAsync(int examples)
        {
            var incremental = await Learner.GetModelAsync(Ct);
            Learner.LastLoad.ShouldBe(LearnerLoadKind.Built, "the first load built it; later calls only replay");
            incremental.ExampleCount.ShouldBe(examples);
            var fresh = await NextSession().RebuildAsync(Ct);
            incremental.ToJson().ShouldBe(fresh.ToJson());
        }

        await _host.Transactions.ApproveAsync([pending.Id], Ct);
        await ShouldMatchRetrainAsync(9);

        await _host.Transactions.CategorizeAsync([pending.Id], dining, Ct);
        await ShouldMatchRetrainAsync(9);
        (await Learner.GetModelAsync(Ct)).PayeeHistory("TRADER JOES")[dining].ShouldBe(1);

        await _host.Transactions.SaveAsync(new SaveTransactionRequest(pending.Id, checking, new DateOnly(2026, 8, 21), -950, "Taco Truck", dining, null), Ct);
        await ShouldMatchRetrainAsync(9);

        await _host.Transactions.SaveAsync(new SaveTransactionRequest(pending.Id, checking, new DateOnly(2026, 8, 21), -950, "Taco Truck", null, null,
            Splits: [new SplitLine(dining, null, -500), new SplitLine(groceries, null, -450)]), Ct);
        await ShouldMatchRetrainAsync(8);

        await _host.Undo.UndoAsync(Ct);
        await ShouldMatchRetrainAsync(9);

        await _host.Transactions.DeleteAsync([pending.Id], Ct);
        await ShouldMatchRetrainAsync(8);

        await _host.Undo.UndoAsync(Ct);
        await _host.Undo.UndoAsync(Ct);
        await _host.Undo.UndoAsync(Ct);
        await ShouldMatchRetrainAsync(9);
    }

    [Fact]
    public async Task A_payee_rename_moves_its_examples_to_the_new_name()
    {
        await HistoryAsync();
        await Learner.GetModelAsync(Ct);
        var payee = (await _host.Payees.SearchAsync("taco", 1, Ct)).Single();

        await _host.Payees.RenameAsync(payee.Id, "Tacos El Gordo", Ct);

        var model = await Learner.GetModelAsync(Ct);
        model.PayeeHistory("Taco Truck").ShouldBeEmpty();
        model.PayeeHistory("Tacos El Gordo").Values.Sum().ShouldBe(4);
        model.ToJson().ShouldBe((await NextSession().RebuildAsync(Ct)).ToJson());
    }

    [Fact]
    public async Task A_different_version_key_or_a_stale_cache_rebuilds()
    {
        await HistoryAsync();
        var model = await Learner.GetModelAsync(Ct);

        // An older normalizer version wrote this cache.
        await using (var db = _host.Db())
        {
            var row = await db.Settings.SingleAsync(s => s.Key == LearnerService.SettingKey);
            row.ValueJson = row.ValueJson.Replace(LearnerService.VersionKey, "normalizer-0.model-1.cache-1", StringComparison.Ordinal);
            await db.SaveChangesAsync();
        }

        var next = NextSession();
        (await next.GetModelAsync(Ct)).ToJson().ShouldBe(model.ToJson());
        next.LastLoad.ShouldBe(LearnerLoadKind.Built);
        (await CachedJsonAsync()).ShouldContain(LearnerService.VersionKey);

        // A write that bypassed the audit log (e.g. a fixture): the example count no longer matches.
        await using (var db = _host.Db())
        {
            var t = await db.Transactions.FirstAsync(t => t.PayeeRaw == "Taco Truck");
            db.Transactions.Add(new Transaction { AccountId = t.AccountId, Date = t.Date, Amount = -100, PayeeId = t.PayeeId, PayeeRaw = t.PayeeRaw, CategoryId = t.CategoryId, IsApproved = true });
            await db.SaveChangesAsync();
        }

        var third = NextSession();
        (await third.GetModelAsync(Ct)).ExampleCount.ShouldBe(9);
        third.LastLoad.ShouldBe(LearnerLoadKind.Built);
    }

    [Fact]
    public async Task Status_reports_preparing_then_ready()
    {
        await HistoryAsync();
        var seen = new List<LearnerStatus>();
        Learner.StatusChanged += (_, _) =>
        {
            lock (seen)
            {
                seen.Add(Learner.Status);
            }
        };

        await Learner.WarmUpAsync(Ct);
        seen.ShouldBe([LearnerStatus.Preparing, LearnerStatus.Ready]);
        LearnerModelJson.CurrentVersion.ShouldBeGreaterThan(0);
    }
}
