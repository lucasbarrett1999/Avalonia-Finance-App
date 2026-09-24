using Keel.Application.Categorization;
using Keel.Application.Ledger;
using Keel.Application.Rules;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Rules;
using Keel.Infrastructure.Categorization;
using Keel.Infrastructure.Tests.Ledger;
using Keel.Infrastructure.Tests.Rules;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Categorization;

public sealed class CategorizationServiceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    private ICategorizationService Service => _host.Get<ICategorizationService>();

    private async Task<TransactionDto> AddUnapprovedAsync(Guid account, long amount, string payee, int day = 20, Guid? category = null) =>
        await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, account, new DateOnly(2026, 8, day), amount, payee, category, null, IsApproved: false), Ct);

    // Four approved "TRADER JOES" in Groceries: enough history for the learner (ADR 0021).
    private async Task<(Guid Checking, Guid Groceries, Guid Dining)> HistoryAsync()
    {
        var checking = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        var dining = await _host.CategoryAsync("Dining");
        for (var i = 0; i < 4; i++)
        {
            await _host.AddAsync(checking.Id, -2_000 - i, "TRADER JOES", groceries, new DateOnly(2026, 8, 2 + i));
        }

        return (checking.Id, groceries, dining);
    }

    [Fact]
    public async Task Learner_suggests_with_confidence_and_explanation_and_never_writes_below_the_threshold()
    {
        var (checking, groceries, _) = await HistoryAsync();
        var txn = await AddUnapprovedAsync(checking, -2_100, "TRADER JOES");
        var unknown = await AddUnapprovedAsync(checking, -2_100, "Brand New Place");

        var result = (await Service.SuggestAsync(txn.Id, Ct))!;
        result.Result.DecidedBy.ShouldBe(CategorizationSource.Learner);
        var primary = result.Primary.ShouldNotBeNull();
        primary.CategoryId.ShouldBe(groceries);
        primary.Confidence.ShouldBeGreaterThanOrEqualTo(0.9);
        primary.Explanation.ShouldBe("Suggested because 4 of 4 past 'TRADER JOES' transactions were Groceries");

        (await Service.SuggestAsync(unknown.Id, Ct))!.Result.Suggestions.ShouldBeEmpty();
        var written = await Service.CategorizeAsync([txn.Id, unknown.Id], Ct);
        written.ShouldBe(new CategorizationWriteResult(2, 1));
        (await _host.Transactions.GetAsync(txn.Id, Ct))!.CategoryId.ShouldBe(groceries);
        (await _host.Transactions.GetAsync(unknown.Id, Ct))!.CategoryId.ShouldBeNull();
        (await _host.Transactions.GetAsync(txn.Id, Ct))!.IsApproved.ShouldBeFalse("categorizing never approves");
    }

    [Fact]
    public async Task A_rule_beats_the_payee_default_which_beats_the_learner()
    {
        var (checking, groceries, dining) = await HistoryAsync();
        var household = await _host.CategoryAsync("Household");
        var txn = await AddUnapprovedAsync(checking, -2_100, "TRADER JOES");
        var payee = (await _host.Payees.SearchAsync("TRADER JOES", 1, Ct)).Single();

        await _host.Payees.SetDefaultCategoryAsync(payee.Id, household, Ct);
        var withDefault = (await Service.SuggestAsync(txn.Id, Ct))!;
        withDefault.Result.DecidedBy.ShouldBe(CategorizationSource.PayeeDefault);
        withDefault.Result.Suggestions.Select(s => (s.CategoryId, s.Source)).ShouldBe([(household, CategorizationSource.PayeeDefault), (groceries, CategorizationSource.Learner)]);
        withDefault.Primary!.Confidence.ShouldBe(0.95);

        await _host.Get<IRuleService>().SaveAsync(RuleServiceTests.CategoryRule("Trader", "trader", dining), Ct);
        var withRule = (await Service.SuggestAsync(txn.Id, Ct))!;
        withRule.Result.DecidedBy.ShouldBe(CategorizationSource.Rule);
        var rule = withRule.Result.Suggestions.ShouldHaveSingleItem();
        rule.CategoryId.ShouldBe(dining);
        rule.Confidence.ShouldBe(1.0);
        rule.Explanation.ShouldBe("Set by rule 'Trader'");
    }

    [Fact]
    public async Task Apply_rules_writes_rule_mutations_only_as_one_undoable_action()
    {
        var (checking, groceries, _) = await HistoryAsync();
        var matching = await AddUnapprovedAsync(checking, -2_100, "Costco Wholesale");
        var learnerOnly = await AddUnapprovedAsync(checking, -2_100, "TRADER JOES");
        await _host.Get<IRuleService>().SaveAsync(new RuleDefinition
        {
            Name = "Costco",
            Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.Contains, "costco")] },
            Actions = new RuleActionSet { Actions = [new SetPayeeAction("Costco"), new SetCategoryAction(groceries), new MarkApprovedAction()] },
        }, Ct);

        var result = await Service.ApplyRulesAsync([matching.Id, learnerOnly.Id], Ct);
        result.ShouldBe(new CategorizationWriteResult(2, 1));
        var after = (await _host.Transactions.GetAsync(matching.Id, Ct))!;
        (after.Payee, after.CategoryId, after.IsApproved).ShouldBe(("Costco", groceries, true));
        (await _host.Transactions.GetAsync(learnerOnly.Id, Ct))!.CategoryId.ShouldBeNull("rules write, the learner only suggests");

        _host.Undo.NextUndo.ShouldBe(LedgerAction.ApplyRules);
        await _host.Undo.UndoAsync(Ct);
        var undone = (await _host.Transactions.GetAsync(matching.Id, Ct))!;
        (undone.Payee, undone.CategoryId, undone.IsApproved).ShouldBe(("Costco Wholesale", (Guid?)null, false));
    }

    [Fact]
    public async Task Approve_writes_the_decision_and_the_learner_learns_from_it()
    {
        var (checking, groceries, dining) = await HistoryAsync();
        var first = await AddUnapprovedAsync(checking, -2_100, "Taco Truck");
        var learner = _host.Get<ILearnerService>();
        (await learner.GetModelAsync(Ct)).PayeeHistory("Taco Truck").ShouldBeEmpty();

        (await Service.ApproveAsync([new ReviewDecision(first.Id, dining)], Ct)).ShouldBe(1);
        var approved = (await _host.Transactions.GetAsync(first.Id, Ct))!;
        (approved.CategoryId, approved.IsApproved).ShouldBe((dining, true));
        (await learner.GetModelAsync(Ct)).PayeeHistory("Taco Truck")[dining].ShouldBe(1);

        _host.Undo.NextUndo.ShouldBe(LedgerAction.Approve);
        await _host.Undo.UndoAsync(Ct);
        (await learner.GetModelAsync(Ct)).PayeeHistory("Taco Truck").ShouldBeEmpty();
        groceries.ShouldNotBe(dining);
    }

    [Fact]
    public async Task Batch_approval_takes_only_primary_suggestions_at_or_above_the_threshold()
    {
        var (checking, groceries, dining) = await HistoryAsync();
        for (var i = 0; i < 3; i++)
        {
            await _host.AddAsync(checking, -900, "Corner Deli", i == 0 ? groceries : dining, new DateOnly(2026, 8, 10 + i));
        }

        var confident = await AddUnapprovedAsync(checking, -2_100, "TRADER JOES");
        var unsure = await AddUnapprovedAsync(checking, -900, "Corner Deli");
        var none = await AddUnapprovedAsync(checking, -900, "Somewhere New");
        var unsureSuggestion = (await Service.SuggestAsync(unsure.Id, Ct))!.Primary;
        (unsureSuggestion is null || unsureSuggestion.Confidence < 0.9).ShouldBeTrue();

        var plan = await Service.PlanBatchApprovalAsync(0.9, Ct);
        plan.ShouldBe([new ReviewDecision(confident.Id, groceries, false)]);
        (await Service.ApproveAsync(plan, Ct)).ShouldBe(1);

        await using var db = _host.Db();
        (await db.Transactions.Where(t => !t.IsApproved).Select(t => t.Id).ToListAsync()).ShouldBe([unsure.Id, none.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task Import_adapter_runs_the_full_pipeline()
    {
        var (checking, groceries, _) = await HistoryAsync();
        var txn = await AddUnapprovedAsync(checking, -2_100, "TRADER JOES");
        await _host.Get<ImportCategorizationAdapter>().ApplyAsync([txn.Id], Ct);
        (await _host.Transactions.GetAsync(txn.Id, Ct))!.CategoryId.ShouldBe(groceries);
    }

    [Fact]
    public async Task Unapproved_transactions_page_oldest_first_across_accounts()
    {
        var checking = await _host.CheckingAsync();
        var card = await _host.AccountAsync("Card", AccountType.CreditCard);
        var expected = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            var account = i % 2 == 0 ? checking.Id : card.Id;
            var txn = await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, account, new DateOnly(2026, 8, 1).AddDays(24 - i), -100 - i, "Shop " + i, null, null, IsApproved: false), Ct);
            expected.Insert(0, txn.Id);
        }

        await _host.AddAsync(checking.Id, -5, "Approved");
        var filter = new RegisterFilter(UnapprovedOnly: true);
        var oldestFirst = new RegisterSort(RegisterSortColumn.Date, Descending: false);
        (await _host.Register.CountAsync(filter, Ct)).ShouldBe(25);
        var pages = new List<Guid>();
        for (var skip = 0; skip < 25; skip += 10)
        {
            pages.AddRange((await _host.Register.GetPageAsync(filter, oldestFirst, skip, 10, Ct)).Select(r => r.Id));
        }

        pages.ShouldBe(expected);
    }
}
