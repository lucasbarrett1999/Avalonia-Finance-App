using Keel.Application.Ledger;
using Keel.Application.Rules;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Rules;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Rules;

public sealed class RetroactiveApplyTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    private IRuleService Rules => _host.Get<IRuleService>();

    private async Task<TransactionDto> AddUnapprovedAsync(Guid account, long amount, string payee, DateOnly date, Guid? category = null) =>
        await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, account, date, amount, payee, category, null, IsApproved: false), Ct);

    private async Task<List<(Guid Id, string Payee, Guid? Category, string? Memo, bool Approved, string Tags, int Splits, Guid? Transfer)>> StateAsync()
    {
        await using var db = _host.Db();
        var rows = await db.Transactions.AsNoTracking().Include(t => t.Splits).OrderBy(t => t.Date).ThenBy(t => t.Id).ToListAsync();
        var payees = await db.Payees.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
        var tags = (await (from tt in db.TransactionTags join g in db.Tags on tt.TagId equals g.Id select new { tt.TransactionId, g.Name }).ToListAsync())
            .ToLookup(t => t.TransactionId, t => t.Name);
        return rows.Select(t => (t.Id, t.PayeeId is { } p ? payees[p] : t.PayeeRaw, t.CategoryId, t.Memo, t.IsApproved,
            string.Join(",", tags[t.Id].Order(StringComparer.Ordinal)), t.Splits.Count, t.TransferAccountId)).ToList();
    }

    private static string Describe(RuleOutcomePreview p) =>
        $"{p.TransactionId}|{p.Changes}|{string.Join(";", p.FieldChanges)}|{string.Join(",", p.RuleNames)}";

    [Fact]
    public async Task Preview_matches_apply_exactly_and_one_undo_reverts_everything()
    {
        var checking = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        var household = await _host.CategoryAsync("Household");
        await AddUnapprovedAsync(checking.Id, -1_000, "AMZN MKTP US*2K4", new DateOnly(2026, 8, 3));
        await AddUnapprovedAsync(checking.Id, -9_000, "AMZN MKTP US*7Q1", new DateOnly(2026, 8, 4));
        await AddUnapprovedAsync(checking.Id, -500, "TRADER JOES", new DateOnly(2026, 8, 5));
        await _host.AddAsync(checking.Id, -700, "Corner Shop", household, new DateOnly(2026, 8, 6));

        var rename = await Rules.SaveAsync(new RuleDefinition
        {
            Name = "Amazon",
            ContinueAfterMatch = true,
            Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.StartsWith, "amzn")] },
            Actions = new RuleActionSet { Actions = [new SetPayeeAction("Amazon"), new AddTagAction("online"), new AppendMemoAction("(auto)")] },
        }, Ct);
        var split = await Rules.SaveAsync(new RuleDefinition
        {
            Name = "Big Amazon",
            Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.EqualTo, "Amazon"), new AmountCondition(AmountOperator.GreaterThan, 5_000)] },
            Actions = new RuleActionSet { Actions = [new SplitByPercentagesAction([new PercentSplitLine(household, 60), new PercentSplitLine(groceries, 40)]), new FlagAction()] },
        }, Ct);
        var trader = await Rules.SaveAsync(RuleServiceTests.CategoryRule("Trader", "trader", groceries), Ct);
        var ids = new[] { rename.Id, split.Id, trader.Id };
        var before = await StateAsync();

        var preview = await Rules.PreviewRetroactiveAsync(ids, RetroactiveScope.All, Ct);
        preview.Examined.ShouldBe(5); // including the opening balance
        preview.Changes.Count.ShouldBe(3);
        var big = preview.Changes.Single(c => c.Amount == -9_000);
        big.Changes.ShouldBe(RuleChanges.Payee | RuleChanges.Memo | RuleChanges.Tags | RuleChanges.Flagged | RuleChanges.Splits);
        big.RuleNames.ShouldBe(["Amazon", "Big Amazon"]);
        big.FieldChanges.Single(f => f.Field == RuleChanges.Payee).ShouldBe(new FieldChange(RuleChanges.Payee, "AMZN MKTP US*7Q1", "Amazon"));
        (await StateAsync()).ShouldBe(before, "a preview writes nothing");

        var result = await Rules.ApplyRetroactivelyAsync(ids, RetroactiveScope.All, Ct);
        result.Changed.ShouldBe(3);
        result.Changes.Select(Describe).ShouldBe(preview.Changes.Select(Describe), "apply writes exactly what the preview showed");

        var after = await StateAsync();
        var bigRow = after.Single(r => r.Splits == 2);
        bigRow.Payee.ShouldBe("Amazon");
        bigRow.Tags.ShouldBe("Flagged,online");
        bigRow.Memo.ShouldBe("(auto)");
        bigRow.Category.ShouldBeNull();
        after.Single(r => r.Payee == "TRADER JOES").Category.ShouldBe(groceries);
        after.Where(r => r.Payee == "Amazon").Count().ShouldBe(2);
        after.Single(r => r.Payee == "Corner Shop").ShouldBe(before.Single(r => r.Payee == "Corner Shop"));
        await using (var db = _host.Db())
        {
            var splits = await db.TransactionSplits.Where(s => s.CategoryId != null).OrderBy(s => s.Amount).Select(s => s.Amount).ToListAsync();
            splits.ShouldBe([-5_400L, -3_600L]);
        }

        // Re-applying is idempotent: nothing left to change.
        (await Rules.PreviewRetroactiveAsync(ids, RetroactiveScope.All, Ct)).Changes.ShouldBeEmpty();

        _host.Undo.NextUndo.ShouldBe(LedgerAction.ApplyRules);
        await _host.Undo.UndoAsync(Ct);
        (await StateAsync()).ShouldBe(before);
        await using (var db = _host.Db())
        {
            (await db.Payees.AnyAsync(p => p.Name == "Amazon")).ShouldBeFalse("the payee the rule created is undone too");
        }
    }

    [Fact]
    public async Task Scope_limits_the_transactions_and_transfers_are_created_with_their_pair()
    {
        var checking = await _host.CheckingAsync();
        var savings = await _host.AccountAsync("Savings", AccountType.Savings);
        await AddUnapprovedAsync(checking.Id, -10_000, "ONLINE TRANSFER TO SAV", new DateOnly(2026, 8, 10));
        await AddUnapprovedAsync(checking.Id, -10_000, "ONLINE TRANSFER TO SAV", new DateOnly(2026, 9, 10));
        var rule = await Rules.SaveAsync(new RuleDefinition
        {
            Name = "Savings sweep",
            Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.Contains, "transfer to sav")] },
            Actions = new RuleActionSet { Actions = [new SetTransferAccountAction(savings.Id), new MarkApprovedAction()] },
        }, Ct);

        var scope = new RetroactiveScope(From: new DateOnly(2026, 9, 1), AccountId: checking.Id, UnapprovedOnly: true);
        var preview = await Rules.PreviewRetroactiveAsync([rule.Id], scope, Ct);
        preview.Examined.ShouldBe(1);
        preview.Changes.ShouldHaveSingleItem().FieldChanges.Single(f => f.Field == RuleChanges.TransferAccount).After.ShouldBe("Savings");

        await Rules.ApplyRetroactivelyAsync([rule.Id], scope, Ct);
        (await _host.Accounts.GetAccountAsync(savings.Id, Ct))!.Balance.Amount.ShouldBe(10_000);
        await using var db = _host.Db();
        var september = await db.Transactions.Where(t => t.AccountId == checking.Id && t.Date == new DateOnly(2026, 9, 10)).SingleAsync();
        september.TransferAccountId.ShouldBe(savings.Id);
        september.IsApproved.ShouldBeTrue();
        september.PayeeRaw.ShouldBe("ONLINE TRANSFER TO SAV", "the descriptor is kept");
        (await db.Transactions.CountAsync(t => t.TransferPairId == september.TransferPairId)).ShouldBe(2);
        (await db.Transactions.SingleAsync(t => t.AccountId == checking.Id && t.Date == new DateOnly(2026, 8, 10))).TransferAccountId.ShouldBeNull();
    }
}
