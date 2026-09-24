using Keel.Application.Accounts;
using Keel.Application.Ledger;
using Keel.Domain;
using Keel.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Ledger;

public sealed class AccountServiceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task Opening_balance_creates_a_starting_balance_in_ready_to_assign_for_on_budget_assets()
    {
        var account = await _host.CheckingAsync(opening: 250_000);

        account.Balance.ShouldBe(new Money(250_000, "USD"));
        account.ClearedBalance.Amount.ShouldBe(250_000);
        account.Group.ShouldBe(AccountGroup.Cash);
        await using var db = _host.Db();
        var txn = await db.Transactions.SingleAsync(t => t.AccountId == account.Id);
        txn.PayeeRaw.ShouldBe("Starting Balance");
        txn.CategoryId.ShouldBe(SystemIds.ReadyToAssignCategory);
        txn.Source.ShouldBe(TransactionSource.System);
        txn.Status.ShouldBe(TransactionStatus.Cleared);
        txn.Date.ShouldBe(new DateOnly(2026, 8, 1));
    }

    [Fact]
    public async Task Tracking_and_credit_starting_balances_are_uncategorized_and_cards_get_a_payment_category()
    {
        var loan = await _host.AccountAsync("Car Loan", AccountType.Loan, -1_500_000);
        var card = await _host.AccountAsync("Visa", AccountType.CreditCard, -45_000);

        loan.IsOnBudget.ShouldBeFalse();
        loan.Group.ShouldBe(AccountGroup.Tracking);
        card.Group.ShouldBe(AccountGroup.Credit);
        await using var db = _host.Db();
        (await db.Transactions.Where(t => t.AccountId == loan.Id || t.AccountId == card.Id).Select(t => t.CategoryId).ToListAsync())
            .ShouldAllBe(c => c == null);
        var payment = await db.Categories.SingleAsync(c => c.LinkedAccountId == card.Id);
        payment.GroupId.ShouldBe(SystemIds.CreditCardPaymentsGroup);
        payment.Name.ShouldBe("Visa");
        payment.IsSystem.ShouldBeTrue();
    }

    [Fact]
    public async Task Zero_opening_balance_creates_no_transaction()
    {
        var account = await _host.CheckingAsync(opening: 0);
        await using var db = _host.Db();
        (await db.Transactions.CountAsync(t => t.AccountId == account.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task Validates_names_currency_and_on_budget_overrides()
    {
        var bad = new CreateAccountRequest(" ", AccountType.Checking, "USD", new DateOnly(2026, 8, 1), 0);
        (await Should.ThrowAsync<LedgerValidationException>(() => _host.Accounts.CreateAccountAsync(bad, Ct))).Error.ShouldBe(LedgerError.AccountNameRequired);
        (await Should.ThrowAsync<LedgerValidationException>(() => _host.Accounts.CreateAccountAsync(bad with { Name = "X", Currency = "US" }, Ct)))
            .Error.ShouldBe(LedgerError.InvalidCurrency);
        (await Should.ThrowAsync<LedgerValidationException>(() => _host.Accounts.CreateAccountAsync(bad with { Name = "X", IsOnBudget = false }, Ct)))
            .Error.ShouldBe(LedgerError.OnBudgetNotAllowed);

        var savings = await _host.Accounts.CreateAccountAsync(bad with { Name = "Rainy day", Type = AccountType.Savings, IsOnBudget = false }, Ct);
        savings.Group.ShouldBe(AccountGroup.Tracking);
    }

    [Fact]
    public async Task Closing_requires_zero_balance_or_confirmation_and_hides_the_account()
    {
        var account = await _host.CheckingAsync(opening: 5_000);

        (await Should.ThrowAsync<LedgerValidationException>(() => _host.Accounts.CloseAccountAsync(account.Id, false, Ct)))
            .Error.ShouldBe(LedgerError.AccountHasBalance);
        await _host.Accounts.CloseAccountAsync(account.Id, closeWithBalance: true, Ct);

        (await _host.Accounts.GetAccountsAsync(includeClosed: false, Ct)).ShouldBeEmpty();
        (await _host.Accounts.GetAccountsAsync(includeClosed: true, Ct)).ShouldHaveSingleItem().IsClosed.ShouldBeTrue();

        await _host.Accounts.ReopenAccountAsync(account.Id, Ct);
        (await _host.Accounts.GetAccountsAsync(includeClosed: false, Ct)).ShouldHaveSingleItem().IsClosed.ShouldBeFalse();

        var empty = await _host.CheckingAsync("Empty", opening: 0);
        await _host.Accounts.CloseAccountAsync(empty.Id, closeWithBalance: false, Ct);
        (await _host.Accounts.GetAccountAsync(empty.Id, Ct))!.IsClosed.ShouldBeTrue();
    }

    [Fact]
    public async Task Reorder_persists_within_a_group()
    {
        var a = await _host.CheckingAsync("A", 0);
        var b = await _host.CheckingAsync("B", 0);
        var c = await _host.CheckingAsync("C", 0);
        var card = await _host.AccountAsync("Card", AccountType.CreditCard);

        await _host.Accounts.ReorderAccountsAsync(AccountGroup.Cash, [c.Id, a.Id, b.Id], Ct);

        var list = await _host.Accounts.GetAccountsAsync(false, Ct);
        list.Select(x => x.Name).ShouldBe(["C", "A", "B", "Card"]);
        list.Single(x => x.Id == card.Id).SortOrder.ShouldBe(0);
    }

    [Fact]
    public async Task Update_renames_the_payment_category_and_guards_on_budget_changes()
    {
        var card = await _host.AccountAsync("Visa", AccountType.CreditCard);
        var updated = await _host.Accounts.UpdateAccountAsync(new UpdateAccountRequest(card.Id, "Visa Gold", true, "annual fee in May"), Ct);
        updated.Name.ShouldBe("Visa Gold");
        updated.Notes.ShouldBe("annual fee in May");
        await using (var db = _host.Db())
        {
            (await db.Categories.SingleAsync(c => c.LinkedAccountId == card.Id)).Name.ShouldBe("Visa Gold");
        }

        (await Should.ThrowAsync<LedgerValidationException>(() => _host.Accounts.UpdateAccountAsync(new UpdateAccountRequest(card.Id, "Visa", false, null), Ct)))
            .Error.ShouldBe(LedgerError.OnBudgetNotAllowed);

        var savings = await _host.AccountAsync("Savings", AccountType.Savings, 1_000);
        (await Should.ThrowAsync<LedgerValidationException>(() => _host.Accounts.UpdateAccountAsync(new UpdateAccountRequest(savings.Id, "Savings", false, null), Ct)))
            .Error.ShouldBe(LedgerError.OnBudgetChangeWithTransactions);
        var empty = await _host.AccountAsync("Jar", AccountType.Cash);
        (await _host.Accounts.UpdateAccountAsync(new UpdateAccountRequest(empty.Id, "Jar", false, null), Ct)).Group.ShouldBe(AccountGroup.Tracking);
    }

    [Fact]
    public async Task Mutations_write_audit_events_and_publish_ledger_changes()
    {
        var account = await _host.CheckingAsync(opening: 1_000);

        await using var db = _host.Db();
        var events = await db.AuditEvents.OrderBy(e => e.Id).ToListAsync();
        events.Select(e => e.EntityType).ShouldBe(["Account", "Payee", "Transaction"], ignoreOrder: true);
        events.ShouldAllBe(e => e.Kind == AuditEventKind.Created && e.BeforeJson == null && e.AfterJson != null);
        events.Single(e => e.EntityType == "Account").AfterJson!.ShouldContain("\"Type\":\"Checking\"");

        var change = _host.Bus.LedgerChanges.ShouldHaveSingleItem();
        change.AccountIds.ShouldBe([account.Id]);
        change.MonthsAffected.ShouldBe([new DateOnly(2026, 8, 1)]);
    }
}
