using Keel.Application.Accounts;
using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Ledger;

/// <summary>Account management (F-ACC-1) over the budget file.</summary>
public sealed class AccountService(IDbContextFactory<KeelDbContext> factory, LedgerWriter writer) : IAccountService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<AccountDto>> GetAccountsAsync(bool includeClosed, CancellationToken ct) =>
        Task.Run(() => LoadAsync(null, includeClosed, ct), ct);

    /// <inheritdoc />
    public Task<AccountDto?> GetAccountAsync(Guid id, CancellationToken ct) =>
        Task.Run(async () => (await LoadAsync(id, includeClosed: true, ct).ConfigureAwait(false)).SingleOrDefault(), ct);

    /// <inheritdoc />
    public async Task<AccountDto> CreateAccountAsync(CreateAccountRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = PayeeNames.Clean(request.Name);
        if (name.Length == 0)
        {
            throw new LedgerValidationException(LedgerError.AccountNameRequired);
        }

        if (!Currency.IsValidCode(request.Currency?.Trim().ToUpperInvariant()))
        {
            throw new LedgerValidationException(LedgerError.InvalidCurrency);
        }

        var isOnBudget = request.IsOnBudget ?? AccountTypeInfo.IsOnBudgetByDefault(request.Type);
        if (!AccountTypeInfo.IsOnBudgetAllowed(request.Type, isOnBudget))
        {
            throw new LedgerValidationException(LedgerError.OnBudgetNotAllowed);
        }

        var id = await writer.RunAsync(
            LedgerAction.CreateAccount,
            async session =>
            {
                var db = session.Db;
                var account = Account.Create(name, request.Type, request.OpeningDate, request.Currency!);
                account.IsOnBudget = isOnBudget;
                account.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
                account.OwnerProfileId = SystemIds.DefaultProfile;
                account.SortOrder = await NextSortOrderAsync(db, account.Group, ct).ConfigureAwait(false);
                db.Accounts.Add(account);

                if (account.IsOnBudget && AccountTypeInfo.IsCredit(account.Type))
                {
                    // Each on-budget credit account has a Credit Card Payment category (PRD 6.4.5).
                    var sort = await db.Categories.Where(c => c.GroupId == SystemIds.CreditCardPaymentsGroup)
                        .Select(c => (int?)c.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
                    db.Categories.Add(new Category
                    {
                        GroupId = SystemIds.CreditCardPaymentsGroup,
                        Name = account.Name,
                        SortOrder = sort + 1,
                        IsSystem = true,
                        LinkedAccountId = account.Id,
                    });
                }

                if (request.OpeningBalance != 0)
                {
                    var payee = await LedgerLookups.GetOrAddPayeeAsync(db, LedgerLookups.StartingBalancePayee, ct).ConfigureAwait(false);
                    db.Transactions.Add(new Transaction
                    {
                        AccountId = account.Id,
                        Date = request.OpeningDate,
                        PayeeId = payee!.Id,
                        PayeeRaw = LedgerLookups.StartingBalancePayee,
                        Amount = request.OpeningBalance,
                        CategoryId = LedgerLookups.SystemInflowCategory(account),
                        Status = TransactionStatus.Cleared,
                        IsApproved = true,
                        Source = TransactionSource.System,
                    });
                }

                return account.Id;
            },
            ct).ConfigureAwait(false);

        return (await GetAccountAsync(id, ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<AccountDto> UpdateAccountAsync(UpdateAccountRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = PayeeNames.Clean(request.Name);
        if (name.Length == 0)
        {
            throw new LedgerValidationException(LedgerError.AccountNameRequired);
        }

        await writer.RunAsync(
            LedgerAction.UpdateAccount,
            async session =>
            {
                var db = session.Db;
                var account = await LedgerLookups.AccountAsync(db, request.Id, ct).ConfigureAwait(false);
                if (request.IsOnBudget != account.IsOnBudget)
                {
                    if (!AccountTypeInfo.IsOnBudgetAllowed(account.Type, request.IsOnBudget))
                    {
                        throw new LedgerValidationException(LedgerError.OnBudgetNotAllowed);
                    }

                    if (await db.Transactions.IgnoreQueryFilters().AnyAsync(t => t.AccountId == account.Id, ct).ConfigureAwait(false))
                    {
                        throw new LedgerValidationException(LedgerError.OnBudgetChangeWithTransactions);
                    }

                    account.IsOnBudget = request.IsOnBudget;
                    account.SortOrder = await NextSortOrderAsync(db, account.Group, ct).ConfigureAwait(false);
                }

                if (account.Name != name)
                {
                    account.Name = name;
                    var payment = await db.Categories.SingleOrDefaultAsync(c => c.LinkedAccountId == account.Id, ct).ConfigureAwait(false);
                    if (payment is not null)
                    {
                        payment.Name = name;
                    }
                }

                account.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
                return true;
            },
            ct).ConfigureAwait(false);

        return (await GetAccountAsync(request.Id, ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public Task CloseAccountAsync(Guid id, bool closeWithBalance, CancellationToken ct) =>
        writer.RunAsync(
            LedgerAction.CloseAccount,
            async session =>
            {
                var db = session.Db;
                var account = await LedgerLookups.AccountAsync(db, id, ct).ConfigureAwait(false);
                if (account.IsClosed)
                {
                    return false;
                }

                var balance = await db.Transactions.Where(t => t.AccountId == id).SumAsync(t => t.Amount, ct).ConfigureAwait(false);
                if (balance != 0 && !closeWithBalance)
                {
                    throw new LedgerValidationException(LedgerError.AccountHasBalance);
                }

                account.IsClosed = true;
                return true;
            },
            ct);

    /// <inheritdoc />
    public Task ReopenAccountAsync(Guid id, CancellationToken ct) =>
        writer.RunAsync(
            LedgerAction.ReopenAccount,
            async session =>
            {
                var account = await LedgerLookups.AccountAsync(session.Db, id, ct).ConfigureAwait(false);
                account.IsClosed = false;
                return true;
            },
            ct);

    /// <inheritdoc />
    public Task ReorderAccountsAsync(AccountGroup group, IReadOnlyList<Guid> orderedIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        return writer.RunAsync(
            LedgerAction.ReorderAccounts,
            async session =>
            {
                var accounts = (await session.Db.Accounts.ToListAsync(ct).ConfigureAwait(false))
                    .Where(a => a.Group == group)
                    .OrderBy(a => orderedIds.Contains(a.Id) ? orderedIds.ToList().IndexOf(a.Id) : int.MaxValue)
                    .ThenBy(a => a.SortOrder)
                    .ToList();
                for (var i = 0; i < accounts.Count; i++)
                {
                    accounts[i].SortOrder = i;
                }

                return true;
            },
            ct);
    }

    private static async Task<int> NextSortOrderAsync(KeelDbContext db, AccountGroup group, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking().Select(a => new { a.Type, a.IsOnBudget, a.SortOrder }).ToListAsync(ct).ConfigureAwait(false);
        var inGroup = accounts.Where(a => AccountTypeInfo.GroupOf(a.Type, a.IsOnBudget) == group).Select(a => a.SortOrder).ToList();
        return inGroup.Count == 0 ? 0 : inGroup.Max() + 1;
    }

    private async Task<IReadOnlyList<AccountDto>> LoadAsync(Guid? id, bool includeClosed, CancellationToken ct)
    {
        var db = factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var query = db.Accounts.AsNoTracking();
            if (id is { } only)
            {
                query = query.Where(a => a.Id == only);
            }
            else if (!includeClosed)
            {
                query = query.Where(a => !a.IsClosed);
            }

            var accounts = await query.ToListAsync(ct).ConfigureAwait(false);
            var ids = accounts.Select(a => a.Id).ToList();
            var balances = await db.Transactions
                .Where(t => ids.Contains(t.AccountId))
                .GroupBy(t => t.AccountId)
                .Select(g => new
                {
                    AccountId = g.Key,
                    Balance = g.Sum(t => t.Amount),
                    Cleared = g.Sum(t => t.Status != TransactionStatus.Uncleared ? t.Amount : 0),
                })
                .ToDictionaryAsync(b => b.AccountId, ct).ConfigureAwait(false);
            var connections = await db.SyncConnections.AsNoTracking()
                .ToDictionaryAsync(c => c.Id, c => c.Status, ct).ConfigureAwait(false);

            return accounts
                .OrderBy(a => a.Group)
                .ThenBy(a => a.SortOrder)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(a =>
                {
                    balances.TryGetValue(a.Id, out var b);
                    SyncStatus? status = a.SyncConnectionId is { } c && connections.TryGetValue(c, out var s) ? s : null;
                    return new AccountDto(
                        a.Id,
                        a.Name,
                        a.Type,
                        a.Group,
                        a.IsOnBudget,
                        a.IsClosed,
                        a.SortOrder,
                        new Money(b?.Balance ?? 0, a.Currency),
                        new Money(b?.Cleared ?? 0, a.Currency),
                        a.ReportedBalance is { } reported ? new Money(reported, a.Currency) : null,
                        status,
                        a.OpeningDate,
                        a.Notes);
                })
                .ToList();
        }
    }
}
