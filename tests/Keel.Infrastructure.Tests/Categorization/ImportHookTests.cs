using Keel.Application.Categorization;
using Keel.Application.Import;
using Keel.Application.Rules;
using Keel.Domain.Entities;
using Keel.Domain.Rules;
using Keel.Infrastructure.Categorization;
using Keel.Infrastructure.Tests.Import.Pipeline;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Categorization;

public sealed class ImportHookTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public void The_rules_hook_is_registered_after_the_no_op_default()
    {
        var hooks = _host.Get<IEnumerable<IImportCategorizationHook>>().ToList();
        hooks.Count.ShouldBe(2);
        hooks[1].ShouldBeOfType<RulesImportCategorizationHook>();
    }

    [Fact]
    public async Task An_import_applies_rule_renames_and_categories_then_payee_defaults_then_the_learner()
    {
        var checking = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        var household = await _host.CategoryAsync("Household");
        var dining = await _host.CategoryAsync("Dining");
        for (var i = 0; i < 3; i++)
        {
            await _host.AddAsync(checking.Id, -1_200 - i, "Taco Truck", dining, new DateOnly(2026, 8, 2 + i));
        }

        await _host.AddAsync(checking.Id, -500, "Hardware Barn", household);
        var hardware = (await _host.Payees.SearchAsync("hardware", 1, Ct)).Single();
        await _host.Payees.SetDefaultCategoryAsync(hardware.Id, household, Ct);
        await _host.Get<IRuleService>().SaveAsync(new RuleDefinition
        {
            Name = "Trader Joe's",
            Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.Contains, "trader jo")] },
            Actions = new RuleActionSet { Actions = [new SetPayeeAction("Trader Joe's"), new SetCategoryAction(groceries), new AppendMemoAction("(rule)")] },
        }, Ct);
        await _host.Get<ILearnerService>().GetModelAsync(Ct);

        var incoming = new[]
        {
            new IncomingTransaction(new DateOnly(2026, 9, 1), -4_250, "TRADER JOE'S #552 PORTLAND OR"),
            new IncomingTransaction(new DateOnly(2026, 9, 2), -1_999, "HARDWARE BARN"),
            new IncomingTransaction(new DateOnly(2026, 9, 3), -1_250, "TACO TRUCK"),
            new IncomingTransaction(new DateOnly(2026, 9, 4), -3_000, "SOMEWHERE NEW"),
        };
        var preview = await _host.Get<IImportService>().PreviewAsync(Keel.Domain.TransactionSource.File, new ImportBatch(checking.Id, incoming), Ct);
        preview.ShouldNotBeNull();
        await using (var db = _host.Db())
        {
            (await db.Transactions.CountAsync(t => t.Date >= new DateOnly(2026, 9, 1))).ShouldBe(0, "a preview writes nothing");
        }

        await _host.ImportAsync(checking.Id, incoming);

        await using var check = _host.Db();
        var rows = await check.Transactions.AsNoTracking().Where(t => t.Date >= new DateOnly(2026, 9, 1)).OrderBy(t => t.Date).ToListAsync();
        var payees = await check.Payees.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
        rows.Select(r => (payees[r.PayeeId!.Value], r.CategoryId, r.IsApproved)).ShouldBe(
        [
            ("Trader Joe's", (Guid?)groceries, false),
            ("Hardware Barn", household, false),
            ("Taco Truck", dining, false),
            ("Somewhere New", null, false),
        ]);
        rows[0].Memo.ShouldBe("(rule)");
        rows[0].PayeeRaw.ShouldBe("TRADER JOE'S #552 PORTLAND OR", "the descriptor is kept");
        (await check.Settings.CountAsync(s => s.Key == LearnerService.SettingKey)).ShouldBe(1);
    }
}
