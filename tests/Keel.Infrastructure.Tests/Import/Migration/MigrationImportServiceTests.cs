using Keel.Application.Accounts;
using Keel.Application.Import;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Infrastructure.Rules;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Import.Migration;

/// <summary>
/// YNAB and Monarch exports through the real migration service and import pipeline on a real SQLite file
/// (ADR 0099): accounts, categories and tags are created, rows keep their category, cleared state and tags,
/// transfers pair across the file's accounts, the preview equals the import, and importing twice adds nothing.
/// </summary>
public sealed class MigrationImportServiceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    private static CancellationToken Ct => CancellationToken.None;

    private IMigrationImportService Migration => _host.Get<IMigrationImportService>();

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Ynab_register_is_planned_with_new_accounts_categories_and_tags()
    {
        var parsed = await MigrationFixtureTests.ParseAsync("ynab-register.csv");
        var plan = await Migration.PlanAsync(parsed, Ct);

        plan.Accounts.Select(a => (a.SourceAccount, a.RowCount, a.SuggestedType)).ShouldBe(
            [("Checking", 8, AccountType.Checking), ("Savings", 2, AccountType.Savings), ("Visa Card", 3, AccountType.CreditCard)]);
        plan.Accounts.ShouldAllBe(a => a.MatchedAccountId == null);
        plan.Accounts[0].Net.ShouldBe(2_500_00 - 84_12 - 61_40 - 200_00 - 350_00 + 1_850_00 - 40_00 - 9_99);
        plan.NewCategories.Select(c => $"{c.Group}: {c.Name}").Order().ShouldBe(
            ["Bills: Electric", "Everyday: Dining Out", "Everyday: Groceries", "Everyday: Household", "Home: Repairs"]);
        plan.NewTags.ShouldBe([SnapshotSource.FlaggedTag]);

        // An existing account with the same name is matched.
        var checking = await _host.CheckingAsync("checking", 0);
        (await Migration.PlanAsync(parsed, Ct)).Accounts[0].MatchedAccountId.ShouldBe(checking.Id);
    }

    [Fact]
    public async Task Ynab_register_imports_every_account_once_and_is_idempotent()
    {
        var parsed = await MigrationFixtureTests.ParseAsync("ynab-register.csv");
        var request = CreateAll(parsed, ("Checking", AccountType.Checking), ("Savings", AccountType.Savings), ("Visa Card", AccountType.CreditCard));

        var preview = await Migration.PreviewAsync(request, Ct);
        (await CountTransactionsAsync()).ShouldBe(0, "a preview writes nothing");
        (await _host.Accounts.GetAccountsAsync(true, Ct)).ShouldBeEmpty();

        var first = await Migration.ImportAsync(request, Ct);
        first.Added.ShouldBe(13);
        first.AccountsCreated.ShouldBe(3);
        first.GroupsCreated.ShouldBe(3);
        first.CategoriesCreated.ShouldBe(5);
        first.TagsCreated.ShouldBe(1);
        first.Transfers.ShouldBe(2, "Checking-Savings and Checking-Visa pair across the file's accounts");
        Summaries(preview).ShouldBe(Summaries(first), "the preview is the import rolled back");

        var accounts = await _host.Accounts.GetAccountsAsync(false, Ct);
        accounts.Select(a => (a.Name, a.Type, a.IsOnBudget)).ShouldBe(
            [("Checking", AccountType.Checking, true), ("Savings", AccountType.Savings, true), ("Visa Card", AccountType.CreditCard, true)], ignoreOrder: true);
        accounts.Single(a => a.Name == "Checking").Balance.Amount.ShouldBe(2_500_00 - 84_12 - 61_40 - 200_00 - 350_00 + 1_850_00 - 40_00 - 9_99);
        accounts.Single(a => a.Name == "Visa Card").Balance.Amount.ShouldBe(-350_00 - 12_30 + 350_00);

        await using (var db = _host.Db())
        {
            var rows = await db.Transactions.AsNoTracking().ToListAsync();
            var categories = await db.Categories.AsNoTracking().Include(c => c.Group).ToDictionaryAsync(c => c.Id, c => $"{c.Group!.Name}: {c.Name}");
            string CategoryOf(string payee) => rows.Single(r => r.PayeeRaw == payee).CategoryId is { } id ? categories[id] : string.Empty;

            CategoryOf("Corner Grocer").ShouldBe("Everyday: Groceries");
            CategoryOf("Acme Payroll").ShouldBe("Inflow: Ready to Assign");
            rows.Single(r => r.PayeeRaw == "Corner Grocer").Status.ShouldBe(TransactionStatus.Reconciled);
            rows.Single(r => r.PayeeRaw == "City Power").Status.ShouldBe(TransactionStatus.Cleared);
            rows.Single(r => r.PayeeRaw == "Bean There Cafe").Status.ShouldBe(TransactionStatus.Uncleared);
            rows.ShouldAllBe(r => r.Source == TransactionSource.File);
            rows.Where(r => r.CategoryId != null || r.TransferPairId != null).ShouldAllBe(r => r.IsApproved);
            rows.Count(r => r.TransferPairId != null).ShouldBe(4);

            var flagged = await db.TransactionTags.AsNoTracking().Join(db.Tags, t => t.TagId, t => t.Id, (link, tag) => new { link.TransactionId, tag.Name }).ToListAsync();
            flagged.ShouldHaveSingleItem().Name.ShouldBe(SnapshotSource.FlaggedTag);
            rows.Single(r => r.Id == flagged[0].TransactionId).PayeeRaw.ShouldBe("City Power");

            // The new credit card has its Credit Card Payment category (PRD 6.4.5).
            var visa = accounts.Single(a => a.Name == "Visa Card").Id;
            (await db.Categories.CountAsync(c => c.LinkedAccountId == visa && c.GroupId == SystemIds.CreditCardPaymentsGroup)).ShouldBe(1);
        }

        // Importing the same export again adds nothing and creates nothing.
        var existing = accounts.ToDictionary(a => a.Name, a => a.Id);
        var again = await Migration.ImportAsync(new MigrationRequest(parsed, "USD",
            parsed.Accounts.Select(a => MigrationAccountChoice.Into(a.Name!, existing[a.Name!])).ToList()), Ct);
        again.Added.ShouldBe(0);
        again.Duplicates.ShouldBe(13);
        again.HasChanges.ShouldBeFalse();
        (await CountTransactionsAsync()).ShouldBe(13);
    }

    [Fact]
    public async Task A_migration_is_one_undoable_action()
    {
        var parsed = await MigrationFixtureTests.ParseAsync("monarch-transactions.csv");
        var summary = await Migration.ImportAsync(CreateAll(parsed, ("Joint Checking (...1234)", AccountType.Checking), ("Rewards Card (...9876)", AccountType.CreditCard)), Ct);
        summary.Added.ShouldBe(8);

        var undo = _host.Get<IUndoService>();
        undo.NextUndo.ShouldBe(LedgerAction.ImportTransactions);
        await undo.UndoAsync(Ct);

        (await CountTransactionsAsync()).ShouldBe(0);
        (await _host.Accounts.GetAccountsAsync(true, Ct)).ShouldBeEmpty();
        await using var db = _host.Db();
        (await db.Tags.CountAsync()).ShouldBe(0);
        (await db.TransactionTags.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        (await db.Categories.CountAsync(c => !c.IsSystem)).ShouldBe(0);
    }

    [Fact]
    public async Task Monarch_rows_get_their_category_group_tags_income_and_transfers()
    {
        var parsed = await MigrationFixtureTests.ParseAsync("monarch-transactions.csv");
        var plan = await Migration.PlanAsync(parsed, Ct);
        plan.Accounts.Select(a => a.SuggestedType).ShouldBe([AccountType.Checking, AccountType.CreditCard]);
        plan.NewCategories.Select(c => $"{c.Group}: {c.Name}").Order().ShouldBe(
            ["Auto & Transport: Gas", "Food & Dining: Groceries", "Other: Pet Supplies", "Travel & Lifestyle: Entertainment & Recreation"]);
        plan.NewTags.Order().ShouldBe(["Household", "Pets", "Subscription", "Weekly"]);

        var first = await Migration.ImportAsync(CreateAll(parsed, ("Joint Checking (...1234)", AccountType.Checking), ("Rewards Card (...9876)", AccountType.CreditCard)), Ct);
        first.Added.ShouldBe(8);
        first.Transfers.ShouldBe(1, "the card payment pairs with its other side");
        first.Uncategorized.ShouldBeGreaterThanOrEqualTo(1);

        await using (var db = _host.Db())
        {
            var rows = await db.Transactions.AsNoTracking().ToListAsync();
            rows.Single(r => r.PayeeRaw == "Acme Payroll").CategoryId.ShouldBe(SystemIds.ReadyToAssignCategory);
            var mystery = rows.Single(r => r.PayeeRaw == "POS 4471 SQ *MYSTERY");
            mystery.CategoryId.ShouldBeNull();
            mystery.IsApproved.ShouldBeFalse("Monarch's Uncategorized row goes to review");
            rows.Where(r => !r.IsApproved).ShouldHaveSingleItem().Id.ShouldBe(mystery.Id);
            rows.Where(r => r.PayeeRaw == "Credit Card Payment").ShouldAllBe(r => r.TransferPairId != null && r.IsApproved);
            var tags = await db.TransactionTags.AsNoTracking().Join(db.Tags, t => t.TagId, t => t.Id, (link, tag) => new { link.TransactionId, tag.Name }).ToListAsync();
            var joes = rows.Single(r => r.PayeeRaw == "Trader Joe's").Id;
            tags.Where(t => t.TransactionId == joes).Select(t => t.Name).Order().ShouldBe(["Household", "Weekly"]);
        }

        // Existing tags are reused by name, ignoring case; re-import adds nothing.
        var accounts = (await _host.Accounts.GetAccountsAsync(false, Ct)).ToDictionary(a => a.Name, a => a.Id);
        var again = await Migration.ImportAsync(new MigrationRequest(parsed, "USD",
            parsed.Accounts.Select(a => MigrationAccountChoice.Into(a.Name!, accounts[a.Name!])).ToList()), Ct);
        again.Added.ShouldBe(0);
        again.TagsCreated.ShouldBe(0);
        again.CategoriesCreated.ShouldBe(0);
    }

    [Fact]
    public async Task Skipped_accounts_and_existing_accounts_and_categories_are_respected()
    {
        var checking = await _host.CheckingAsync("Main", 0);
        var groceries = await _host.CategoryAsync("groceries", "EVERYDAY");
        var parsed = await MigrationFixtureTests.ParseAsync("ynab-register.csv");
        var summary = await Migration.ImportAsync(new MigrationRequest(parsed, "USD",
        [
            MigrationAccountChoice.Into("Checking", checking.Id),
            MigrationAccountChoice.Skipped("Savings"),
            MigrationAccountChoice.Skipped("Visa Card"),
        ]), Ct);

        summary.Accounts.ShouldHaveSingleItem().AccountId.ShouldBe(checking.Id);
        summary.Added.ShouldBe(8);
        summary.AccountsCreated.ShouldBe(0);
        summary.CategoriesCreated.ShouldBe(3, "Groceries exists (any case); Electric, Repairs and Household are new");
        await using var db = _host.Db();
        (await db.Transactions.SingleAsync(t => t.PayeeRaw == "Corner Grocer")).CategoryId.ShouldBe(groceries);
        (await db.Transactions.CountAsync(t => t.TransferPairId != null)).ShouldBe(0, "the other sides were skipped");
    }

    [Fact]
    public async Task A_closed_or_missing_account_and_a_tracking_type_for_a_new_account_are_refused()
    {
        var parsed = await MigrationFixtureTests.ParseAsync("ynab-register.csv");
        await Should.ThrowAsync<Keel.Application.Ledger.LedgerValidationException>(() => Migration.ImportAsync(
            new MigrationRequest(parsed, "USD", [MigrationAccountChoice.Into("Checking", Guid.NewGuid())]), Ct));
        await Should.ThrowAsync<Keel.Application.Ledger.LedgerValidationException>(() => Migration.ImportAsync(
            new MigrationRequest(parsed, "USD", [MigrationAccountChoice.Create("Checking", "Brokerage", AccountType.Investment)]), Ct));
        (await CountTransactionsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Ynab_budget_export_writes_assignments_once()
    {
        // The card exists (from the register), so its payment row maps to its category.
        var register = await MigrationFixtureTests.ParseAsync("ynab-register.csv");
        await Migration.ImportAsync(CreateAll(register, ("Checking", AccountType.Checking), ("Savings", AccountType.Savings), ("Visa Card", AccountType.CreditCard)), Ct);

        var budget = await MigrationFixtureTests.ParseAsync("ynab-budget.csv");
        var preview = await Migration.PreviewBudgetAsync(budget, Ct);
        var first = await Migration.ImportBudgetAsync(budget, Ct);
        preview.ShouldBe(first);
        first.Months.ShouldBe(2);
        first.FirstMonth.ShouldBe(new DateOnly(2026, 1, 1));
        first.LastMonth.ShouldBe(new DateOnly(2026, 2, 1));
        first.Assignments.ShouldBe(6);
        first.Unchanged.ShouldBe(1, "the card payment row assigns 0");
        first.Skipped.ShouldBe(1, "Inflow: Ready to Assign is computed, not assigned");
        first.GroupsCreated.ShouldBe(1);
        first.CategoriesCreated.ShouldBe(1, "Fun: Concerts");
        _host.Get<IUndoService>().NextUndo.ShouldBe(LedgerAction.AssignBudget);

        await using (var db = _host.Db())
        {
            var assigned = await db.BudgetAssignments.AsNoTracking().Join(db.Categories, b => b.CategoryId, c => c.Id, (b, c) => new { c.Name, b.Month, b.Assigned }).ToListAsync();
            assigned.Where(a => a.Name == "Groceries").OrderBy(a => a.Month).Select(a => a.Assigned).ShouldBe([400_00, 450_00]);
            assigned.Sum(a => a.Assigned).ShouldBe(400_00 + 100_00 + 70_00 + 450_00 + 70_00 + 25_00);
        }

        var again = await Migration.ImportBudgetAsync(budget, Ct);
        again.Assignments.ShouldBe(0);
        again.Unchanged.ShouldBe(7);
        again.HasChanges.ShouldBeFalse();
    }

    private static MigrationRequest CreateAll(ParseResult parsed, params (string Source, AccountType Type)[] accounts) =>
        new(parsed, "USD", accounts.Select(a => MigrationAccountChoice.Create(a.Source, a.Source, a.Type)).ToList());

    private static List<string> Summaries(MigrationSummary summary) =>
        summary.Accounts.Select(a => $"{a.SourceAccount} {a.AccountName} {a.Created} {a.Summary.Added} {a.Summary.DuplicatesSkipped} {a.Summary.TransfersMatched} {a.Summary.Uncategorized}")
            .Append($"{summary.GroupsCreated} {summary.CategoriesCreated} {summary.TagsCreated}")
            .ToList();

    private async Task<int> CountTransactionsAsync()
    {
        await using var db = _host.Db();
        return await db.Transactions.IgnoreQueryFilters().CountAsync();
    }
}
