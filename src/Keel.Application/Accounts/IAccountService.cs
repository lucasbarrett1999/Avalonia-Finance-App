using Keel.Domain;

namespace Keel.Application.Accounts;

/// <summary>Account management use cases (F-ACC-1). Implemented in M1.</summary>
public interface IAccountService
{
    /// <summary>Lists accounts in sidebar order, with ledger-derived balances.</summary>
    Task<IReadOnlyList<AccountDto>> GetAccountsAsync(bool includeClosed, CancellationToken ct);

    /// <summary>Gets one account, or null when it does not exist.</summary>
    Task<AccountDto?> GetAccountAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Creates an account. A non-zero opening balance creates a system "Starting Balance"
    /// transaction on the opening date.
    /// </summary>
    Task<AccountDto> CreateAccountAsync(CreateAccountRequest request, CancellationToken ct);

    /// <summary>Edits name, on-budget flag (Savings and Cash only), and notes.</summary>
    Task<AccountDto> UpdateAccountAsync(UpdateAccountRequest request, CancellationToken ct);

    /// <summary>Closes an account; a non-zero balance requires <paramref name="closeWithBalance"/>.</summary>
    Task CloseAccountAsync(Guid id, bool closeWithBalance, CancellationToken ct);

    /// <summary>Reopens a closed account.</summary>
    Task ReopenAccountAsync(Guid id, CancellationToken ct);

    /// <summary>Persists a new order for the accounts of one sidebar group.</summary>
    Task ReorderAccountsAsync(AccountGroup group, IReadOnlyList<Guid> orderedIds, CancellationToken ct);
}

/// <summary>An account as shown in the sidebar and registers.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="Type">Account type.</param>
/// <param name="Group">Sidebar group.</param>
/// <param name="IsOnBudget">On-budget flag.</param>
/// <param name="IsClosed">Closed flag.</param>
/// <param name="SortOrder">Order within the group.</param>
/// <param name="Balance">Ledger balance (all non-deleted transactions).</param>
/// <param name="ClearedBalance">Cleared and reconciled balance.</param>
/// <param name="ReportedBalance">Provider-reported balance, when linked.</param>
/// <param name="SyncStatus">Health of the linked connection, when linked.</param>
public sealed record AccountDto(
    Guid Id,
    string Name,
    AccountType Type,
    AccountGroup Group,
    bool IsOnBudget,
    bool IsClosed,
    int SortOrder,
    Money Balance,
    Money ClearedBalance,
    Money? ReportedBalance,
    SyncStatus? SyncStatus);

/// <summary>Input for <see cref="IAccountService.CreateAccountAsync"/>.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Type">Account type.</param>
/// <param name="Currency">ISO 4217 code.</param>
/// <param name="OpeningDate">Date of the opening balance.</param>
/// <param name="OpeningBalance">Opening balance in minor units (liabilities are negative).</param>
/// <param name="IsOnBudget">Override of the type default; null keeps the default.</param>
/// <param name="Notes">Notes.</param>
public sealed record CreateAccountRequest(
    string Name,
    AccountType Type,
    string Currency,
    DateOnly OpeningDate,
    long OpeningBalance,
    bool? IsOnBudget = null,
    string? Notes = null);

/// <summary>Input for <see cref="IAccountService.UpdateAccountAsync"/>.</summary>
/// <param name="Id">Account.</param>
/// <param name="Name">New name.</param>
/// <param name="IsOnBudget">New on-budget flag (only Savings and Cash may differ from the default).</param>
/// <param name="Notes">New notes.</param>
public sealed record UpdateAccountRequest(Guid Id, string Name, bool IsOnBudget, string? Notes);
