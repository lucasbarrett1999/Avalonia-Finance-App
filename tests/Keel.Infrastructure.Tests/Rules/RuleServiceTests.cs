using Keel.Application.Ledger;
using Keel.Application.Rules;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Rules;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Rules;

public sealed class RuleServiceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    private IRuleService Rules => _host.Get<IRuleService>();

    internal static RuleDefinition CategoryRule(string name, string payeeContains, Guid category, bool enabled = true) => new()
    {
        Name = name,
        IsEnabled = enabled,
        Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.Contains, payeeContains)] },
        Actions = new RuleActionSet { Actions = [new SetCategoryAction(category)] },
    };

    [Fact]
    public async Task Create_appends_in_order_and_stores_versioned_rule_json()
    {
        var groceries = await _host.CategoryAsync("Groceries");
        var first = await Rules.SaveAsync(CategoryRule("Trader", "trader", groceries), Ct);
        var second = await Rules.SaveAsync(CategoryRule("Whole", "whole", groceries), Ct);

        first.SortOrder.ShouldBe(0);
        second.SortOrder.ShouldBe(1);
        (await Rules.GetRulesAsync(Ct)).Select(r => r.Name).ShouldBe(["Trader", "Whole"]);
        first.Summary.ShouldBe("If payee contains \"trader\": set category to Groceries");

        await using var db = _host.Db();
        var stored = await db.Rules.SingleAsync(r => r.Id == first.Id);
        stored.ConditionsJson.ShouldStartWith("{\"version\":1");
        stored.ActionsJson.ShouldContain("\"type\":\"setCategory\"");
        RuleDefinition.FromEntity(stored).Actions.Actions.ShouldHaveSingleItem().ShouldBe(new SetCategoryAction(groceries));
        (await db.AuditEvents.CountAsync(a => a.EntityType == nameof(Rule))).ShouldBe(2);
        _host.Bus.Messages.OfType<RulesChanged>().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Invalid_rules_are_refused_with_the_validator_problems_and_nothing_is_written()
    {
        var bad = new RuleDefinition
        {
            Name = " ",
            Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.Regex, "(unclosed")] },
            Actions = new RuleActionSet { Actions = [new SetCategoryAction(Guid.NewGuid())] },
        };

        var ex = await Should.ThrowAsync<RuleValidationException>(() => Rules.SaveAsync(bad, Ct));
        ex.Problems.Select(p => p.Code).ShouldBe([RuleProblemCode.NameMissing, RuleProblemCode.InvalidRegex, RuleProblemCode.UnknownCategory], ignoreOrder: true);
        (await Rules.GetRulesAsync(Ct)).ShouldBeEmpty();
        _host.Undo.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public async Task Update_enable_move_reorder_delete_are_each_undoable()
    {
        var groceries = await _host.CategoryAsync("Groceries");
        var a = await Rules.SaveAsync(CategoryRule("A", "a", groceries), Ct);
        var b = await Rules.SaveAsync(CategoryRule("B", "b", groceries), Ct);
        var c = await Rules.SaveAsync(CategoryRule("C", "c", groceries), Ct);

        await Rules.SaveAsync(a.Definition! with { Name = "A2", ContinueAfterMatch = true }, Ct);
        (await Rules.GetAsync(a.Id, Ct))!.ShouldSatisfyAllConditions(r => r.Name.ShouldBe("A2"), r => r.ContinueAfterMatch.ShouldBeTrue(), r => r.SortOrder.ShouldBe(0));

        await Rules.SetEnabledAsync(b.Id, false, Ct);
        (await Rules.GetAsync(b.Id, Ct))!.IsEnabled.ShouldBeFalse();

        await Rules.MoveAsync(c.Id, -1, Ct);
        (await Rules.GetRulesAsync(Ct)).Select(r => r.Name).ShouldBe(["A2", "C", "B"]);
        await Rules.MoveAsync(c.Id, -5, Ct);
        (await Rules.GetRulesAsync(Ct)).Select(r => r.Name).ShouldBe(["C", "A2", "B"]);

        await Rules.ReorderAsync([b.Id, a.Id, c.Id], Ct);
        var ordered = await Rules.GetRulesAsync(Ct);
        ordered.Select(r => r.Name).ShouldBe(["B", "A2", "C"]);
        ordered.Select(r => r.SortOrder).ShouldBe([0, 1, 2]);

        await Rules.DeleteAsync(b.Id, Ct);
        (await Rules.GetRulesAsync(Ct)).Select(r => r.Name).ShouldBe(["A2", "C"]);

        _host.Undo.NextUndo.ShouldBe(LedgerAction.DeleteRule);
        await _host.Undo.UndoAsync(Ct);
        (await Rules.GetRulesAsync(Ct)).Select(r => r.Name).ShouldBe(["B", "A2", "C"]);
        _host.Undo.NextUndo.ShouldBe(LedgerAction.ReorderRules);
        await _host.Undo.UndoAsync(Ct);
        (await Rules.GetRulesAsync(Ct)).Select(r => r.Name).ShouldBe(["C", "A2", "B"]);
        await _host.Undo.UndoAsync(Ct);
        await _host.Undo.UndoAsync(Ct);
        (await Rules.GetRulesAsync(Ct)).Select(r => r.Name).ShouldBe(["A2", "B", "C"]);
        await _host.Undo.UndoAsync(Ct);
        (await Rules.GetAsync(b.Id, Ct))!.IsEnabled.ShouldBeTrue();
        await _host.Undo.UndoAsync(Ct);
        (await Rules.GetAsync(a.Id, Ct))!.Name.ShouldBe("A");
    }

    [Fact]
    public async Task Unreadable_rules_are_listed_with_their_error_and_skipped_by_the_engine()
    {
        var groceries = await _host.CategoryAsync("Groceries");
        await Rules.SaveAsync(CategoryRule("Good", "x", groceries), Ct);
        await using (var db = _host.Db())
        {
            db.Rules.Add(new Rule { Name = "From the future", SortOrder = 5, ConditionsJson = "{\"version\":99,\"conditions\":[]}", ActionsJson = "{\"version\":1,\"actions\":[]}" });
            await db.SaveChangesAsync();
        }

        var rules = await Rules.GetRulesAsync(Ct);
        rules.Count.ShouldBe(2);
        var future = rules.Single(r => r.Name == "From the future");
        future.IsReadable.ShouldBeFalse();
        future.FormatError.ShouldNotBeNullOrWhiteSpace();
        (await Rules.PreviewRetroactiveAsync(rules.Select(r => r.Id).ToList(), RetroactiveScope.All, Ct)).Changes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Match_count_and_test_report_matching_transactions_newest_first()
    {
        var checking = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        await _host.AddAsync(checking.Id, -1_000, "Trader Joe's #123", date: new DateOnly(2026, 8, 2));
        await _host.AddAsync(checking.Id, -2_000, "TRADER JOES 552", date: new DateOnly(2026, 8, 5));
        await _host.AddAsync(checking.Id, -3_000, "Costco", date: new DateOnly(2026, 8, 6));
        var rule = CategoryRule("Trader", "trader", groceries);

        (await Rules.CountMatchesAsync(rule, Ct)).ShouldBe(2);
        var test = await Rules.TestAsync(rule, 1, Ct);
        test.Matched.ShouldBe(2);
        test.Examined.ShouldBe(4);
        var sample = test.Samples.ShouldHaveSingleItem();
        sample.Payee.ShouldBe("TRADER JOES 552");
        sample.Changes.ShouldBe(RuleChanges.Category);
        sample.FieldChanges.ShouldHaveSingleItem().ShouldBe(new FieldChange(RuleChanges.Category, null, "Groceries"));
        _host.Undo.CanUndo.ShouldBeTrue("only the setup was recorded");
        _host.Undo.NextUndo.ShouldBe(LedgerAction.AddTransaction);
    }

    [Fact]
    public async Task Suggest_from_transaction_prefills_a_valid_rule()
    {
        var checking = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        var txn = await _host.AddAsync(checking.Id, -4_250, "TRADER JOE'S #552", groceries);

        var suggested = await Rules.SuggestFromTransactionAsync(txn.Id, Ct);
        suggested.Id.ShouldBe(Guid.Empty);
        suggested.Actions.Actions.ShouldContain(new SetCategoryAction(groceries));
        suggested.Name.ShouldContain("Groceries");
        (await Rules.ValidateAsync(suggested, Ct)).Where(p => p.Severity == RuleProblemSeverity.Error).ShouldBeEmpty();
        var saved = await Rules.SaveAsync(suggested, Ct);
        (await Rules.CountMatchesAsync(saved.Definition!, Ct)).ShouldBe(1);
    }
}
