using Keel.Application.Categorization;
using Keel.Application.Ledger;
using Keel.Application.Payees;
using Keel.Application.Rules;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Rules;
using Keel.Infrastructure.Categorization;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Categorization;

/// <summary>F-TXN-9 payee merge on real SQLite: everything that refers to a payee moves, one undo reverts it, the learner follows.</summary>
public sealed class PayeeMergeTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    private static CancellationToken Ct => CancellationToken.None;

    private IPayeeService Payees => _host.Payees;

    private IRuleService Rules => _host.Get<IRuleService>();

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<Guid> PayeeAsync(string name) => (await Payees.SearchAsync(name, 1, Ct)).Single().Id;

    private async Task<string> StateAsync()
    {
        await using var db = _host.Db();
        var payees = await db.Payees.AsNoTracking().OrderBy(p => p.Name).Select(p => $"{p.Name}:{p.DefaultCategoryId}").ToListAsync();
        var transactions = await db.Transactions.IgnoreQueryFilters().AsNoTracking().OrderBy(t => t.Id).Select(t => $"{t.Id}:{t.PayeeId}:{t.PayeeRaw}").ToListAsync();
        var scheduled = await db.ScheduledTransactions.AsNoTracking().OrderBy(s => s.Id).Select(s => $"{s.Id}:{s.PayeeId}").ToListAsync();
        var recurring = await db.RecurringItems.AsNoTracking().OrderBy(r => r.Id).Select(r => $"{r.Id}:{r.PayeeId}").ToListAsync();
        var rules = await db.Rules.AsNoTracking().OrderBy(r => r.Id).Select(r => r.ConditionsJson + r.ActionsJson).ToListAsync();
        return string.Join("\n", payees.Concat(transactions).Concat(scheduled).Concat(recurring).Concat(rules));
    }

    [Fact]
    public async Task Merge_moves_transactions_schedules_recurring_items_rules_and_defaults_and_one_undo_reverts_it()
    {
        var checking = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        var household = await _host.CategoryAsync("Household");
        await _host.AddAsync(checking.Id, -1_000, "Amazon", null);
        var amzn = await _host.AddAsync(checking.Id, -2_000, "AMZN Mktp", groceries);
        var deleted = await _host.AddAsync(checking.Id, -3_000, "AMZN Mktp", groceries);
        await _host.Transactions.DeleteAsync([deleted.Id], Ct);
        await _host.AddAsync(checking.Id, -4_000, "Amazon.com", household);
        var amazon = await PayeeAsync("Amazon");
        var mktp = await PayeeAsync("AMZN Mktp");
        var dotcom = await PayeeAsync("Amazon.com");
        await Payees.SetDefaultCategoryAsync(mktp, groceries, Ct);
        await Payees.SetDefaultCategoryAsync(dotcom, household, Ct);

        await using (var db = _host.Db())
        {
            db.ScheduledTransactions.Add(new ScheduledTransaction
            {
                AccountId = checking.Id,
                Amount = -1_299,
                PayeeId = mktp,
                RecurrenceRule = "FREQ=MONTHLY;BYMONTHDAY=5",
                NextDate = new DateOnly(2026, 9, 5),
            });
            db.RecurringItems.Add(new RecurringItem
            {
                PayeeId = dotcom,
                AccountId = checking.Id,
                Cadence = RecurrenceCadence.Monthly,
                ExpectedAmount = -1_299,
                NextExpectedDate = new DateOnly(2026, 9, 5),
                LastSeenDate = new DateOnly(2026, 8, 5),
                Confidence = 0.9,
                Status = RecurringStatus.Active,
            });
            await db.SaveChangesAsync();
        }

        var setPayee = await Rules.SaveAsync(new RuleDefinition
        {
            Name = "Rename",
            Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.EqualTo, "amzn mktp"), new PayeeCondition(TextOperator.Contains, "AMZN")] },
            Actions = new RuleActionSet { Actions = [new SetPayeeAction("Amazon.com")] },
        }, Ct);
        var untouched = await Rules.SaveAsync(RuleServiceTestsCategoryRule("Other", "shop", groceries), Ct);
        var before = await StateAsync();

        var preview = await Payees.PreviewMergeAsync([mktp, amazon, dotcom], amazon, Ct);
        preview.Survivor.Id.ShouldBe(amazon);
        preview.Merged.Select(p => p.Name).ShouldBe(["AMZN Mktp", "Amazon.com"], "list order, survivor excluded");
        preview.Transactions.ShouldBe(2, "deleted rows move too but are not counted");
        preview.ScheduledTransactions.ShouldBe(1);
        preview.RecurringItems.ShouldBe(1);
        preview.Rules.ShouldBe(1);
        preview.DefaultCategoryId.ShouldBe(groceries, "the survivor has none, so the first merged payee's default wins");
        (await StateAsync()).ShouldBe(before, "a preview writes nothing");
        _host.Bus.Messages.Clear();

        var result = await Payees.MergeAsync([mktp, amazon, dotcom], amazon, Ct);

        result.ShouldBe(new PayeeMergeResult(new PayeeDto(amazon, "Amazon", groceries), 2, 2, 1, 1, 1));
        _host.Bus.Messages.OfType<RulesChanged>().ShouldNotBeEmpty();
        await using (var db = _host.Db())
        {
            (await db.Payees.Where(p => p.IsTransferPayeeForAccountId == null && p.Name != "Starting Balance").Select(p => p.Name).ToListAsync()).ShouldBe(["Amazon"]);
            (await db.Transactions.IgnoreQueryFilters().Where(t => t.PayeeId == amazon).CountAsync()).ShouldBe(4);
            (await db.ScheduledTransactions.SingleAsync()).PayeeId.ShouldBe(amazon);
            (await db.RecurringItems.SingleAsync()).PayeeId.ShouldBe(amazon);
            (await db.Transactions.IgnoreQueryFilters().SingleAsync(t => t.Id == amzn.Id)).PayeeRaw.ShouldBe("AMZN Mktp", "the descriptor is kept");
        }

        var rule = (await Rules.GetAsync(setPayee.Id, Ct))!.Definition!;
        rule.Conditions.Conditions.ShouldBe([new PayeeCondition(TextOperator.EqualTo, "Amazon"), new PayeeCondition(TextOperator.Contains, "AMZN")]);
        rule.Actions.Actions.ShouldBe([new SetPayeeAction("Amazon")]);
        RuleJson.Serialize((await Rules.GetAsync(untouched.Id, Ct))!.Definition!.Conditions).ShouldBe(RuleJson.Serialize(untouched.Definition!.Conditions));
        var register = await _host.Register.GetPageAsync(new RegisterFilter(Search: "payee:amazon"), RegisterSort.Default, 0, 200, Ct);
        register.Count.ShouldBe(3);

        _host.Undo.NextUndo.ShouldBe(LedgerAction.MergePayees);
        await _host.Undo.UndoAsync(Ct);
        (await StateAsync()).ShouldBe(before);
        await _host.Undo.RedoAsync(Ct);
        (await Payees.ListAsync(null, 50, Ct)).Where(p => p.Name.Contains("Amaz", StringComparison.Ordinal) || p.Name.Contains("AMZN", StringComparison.Ordinal))
            .Select(p => p.Name).ShouldBe(["Amazon"]);
    }

    [Fact]
    public async Task The_survivors_default_category_wins()
    {
        var checking = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        var household = await _host.CategoryAsync("Household");
        await _host.AddAsync(checking.Id, -1_000, "Costco");
        await _host.AddAsync(checking.Id, -1_000, "COSTCO WHSE");
        var costco = await PayeeAsync("Costco");
        var whse = await PayeeAsync("COSTCO WHSE");
        await Payees.SetDefaultCategoryAsync(costco, household, Ct);
        await Payees.SetDefaultCategoryAsync(whse, groceries, Ct);

        var result = await Payees.MergeAsync([whse], costco, Ct);

        result.Survivor.DefaultCategoryId.ShouldBe(household);
    }

    [Fact]
    public async Task Invalid_merges_are_refused()
    {
        var checking = await _host.CheckingAsync();
        await _host.AccountAsync("Savings", AccountType.Savings);
        await _host.AddAsync(checking.Id, -1_000, "Costco");
        var costco = await PayeeAsync("Costco");

        (await Should.ThrowAsync<LedgerValidationException>(() => Payees.MergeAsync([costco], costco, Ct))).Error.ShouldBe(LedgerError.PayeeMergeInvalid);
        (await Should.ThrowAsync<LedgerValidationException>(() => Payees.MergeAsync([Guid.NewGuid()], costco, Ct))).Error.ShouldBe(LedgerError.PayeeNotFound);
        await using var db = _host.Db();
        var transferPayee = await db.Payees.Where(p => p.IsTransferPayeeForAccountId != null).Select(p => p.Id).FirstOrDefaultAsync();
        if (transferPayee != Guid.Empty)
        {
            (await Should.ThrowAsync<LedgerValidationException>(() => Payees.MergeAsync([transferPayee], costco, Ct))).Error.ShouldBe(LedgerError.PayeeMergeInvalid);
        }
    }

    [Fact]
    public async Task The_learner_follows_a_merge_and_its_undo_through_the_audit_log()
    {
        var checking = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        var dining = await _host.CategoryAsync("Dining");
        for (var i = 0; i < 4; i++)
        {
            await _host.AddAsync(checking.Id, -2_000 - i, "TRADER JOES", groceries, new DateOnly(2026, 8, 2 + i));
            await _host.AddAsync(checking.Id, -1_500 - i, "Tacos El Gordo", dining, new DateOnly(2026, 8, 2 + i));
            await _host.AddAsync(checking.Id, -900 - i, "Taco Truck", dining, new DateOnly(2026, 8, 2 + i));
        }

        var learner = (LearnerService)_host.Get<ILearnerService>();
        await learner.GetModelAsync(Ct);
        LearnerService NextSession() => new(_host.Factory, _host.Get<Keel.Application.Files.IBudgetFileService>());

        await Payees.MergeAsync([await PayeeAsync("Taco Truck")], await PayeeAsync("Tacos El Gordo"), Ct);
        var merged = await learner.GetModelAsync(Ct);
        merged.PayeeHistory("Taco Truck").ShouldBeEmpty();
        merged.PayeeHistory("Tacos El Gordo")[dining].ShouldBe(8);
        merged.PayeeHistory("TRADER JOES")[groceries].ShouldBe(4);
        merged.ToJson().ShouldBe((await NextSession().RebuildAsync(Ct)).ToJson());

        await _host.Undo.UndoAsync(Ct);
        var undone = await learner.GetModelAsync(Ct);
        undone.PayeeHistory("Taco Truck")[dining].ShouldBe(4);
        undone.PayeeHistory("Tacos El Gordo")[dining].ShouldBe(4);
        undone.ToJson().ShouldBe((await NextSession().RebuildAsync(Ct)).ToJson());
    }

    private static RuleDefinition RuleServiceTestsCategoryRule(string name, string payeeContains, Guid category) =>
        Keel.Infrastructure.Tests.Rules.RuleServiceTests.CategoryRule(name, payeeContains, category);
}
