using Keel.Domain;

namespace Keel.Application.Accounts;

/// <summary>Account management use cases (F-ACC-1). Every mutation is audited and undoable.</summary>
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

    /// <summary>
    /// Closes an account; a non-zero balance requires <paramref name="closeWithBalance"/>, otherwise
    /// <see cref="Ledger.LedgerValidationException"/> with <see cref="Ledger.LedgerError.AccountHasBalance"/>.
    /// </summary>
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
/// <param name="OpeningDate">Date of the opening balance.</param>
/// <param name="Notes">Notes.</param>
/// <param name="InterestRateBps">Debt interest rate, annual, in basis points (F-GOAL-2); null when unknown.</param>
/// <param name="MinimumPayment">Debt minimum monthly payment in minor units (F-GOAL-2); null when unknown.</param>
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
    SyncStatus? SyncStatus,
    DateOnly OpeningDate = default,
    string? Notes = null,
    int? InterestRateBps = null,
    long? MinimumPayment = null)
{
    /// <summary>Balance of uncleared transactions (ledger minus cleared).</summary>
    public Money UnclearedBalance => Balance - ClearedBalance;

    /// <summary>Whether the account is a liability (balances are money owed).</summary>
    public bool IsLiability => AccountTypeInfo.IsLiability(Type);

    /// <summary>Whether the user may change the on-budget flag of this type (Savings and Cash).</summary>
    public bool CanOverrideOnBudget => AccountTypeInfo.CanOverrideOnBudget(Type);

    /// <summary>Whether the account can carry debt terms (rate and minimum payment): liability types.</summary>
    public bool CanHaveDebtTerms => DebtTerms.AppliesTo(Type);
}

/// <summary>Interest rate and minimum payment of a debt account (F-GOAL-2); either may be unknown.</summary>
/// <param name="InterestRateBps">Annual rate in basis points (0 to 10,000, i.e. 0% to 100%), or null.</param>
/// <param name="MinimumPayment">Minimum monthly payment in minor units (≥ 0), or null.</param>
public sealed record DebtTerms(int? InterestRateBps, long? MinimumPayment)
{
    /// <summary>No terms.</summary>
    public static DebtTerms None { get; } = new(null, null);

    /// <summary>Whether accounts of <paramref name="type"/> carry debt terms (liabilities: cards, lines of credit, loans and mortgages).</summary>
    public static bool AppliesTo(AccountType type) => AccountTypeInfo.IsLiability(type);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for a rate outside 0–100% or a negative minimum.</summary>
    public void Validate()
    {
        if (InterestRateBps is < 0 or > Keel.Domain.Debt.DebtPayoffCalculator.MaxRateBps)
        {
            throw new ArgumentOutOfRangeException(nameof(InterestRateBps), InterestRateBps, "The interest rate must be between 0% and 100%.");
        }

        if (MinimumPayment is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumPayment), MinimumPayment, "The minimum payment cannot be negative.");
        }
    }
}

/// <summary>Input for <see cref="IAccountService.CreateAccountAsync"/>.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Type">Account type.</param>
/// <param name="Currency">ISO 4217 code.</param>
/// <param name="OpeningDate">Date of the opening balance.</param>
/// <param name="OpeningBalance">Opening balance in minor units (liabilities are negative).</param>
/// <param name="IsOnBudget">Override of the type default; null keeps the default.</param>
/// <param name="Notes">Notes.</param>
/// <param name="Debt">Interest rate and minimum payment (liability types only; ignored otherwise).</param>
public sealed record CreateAccountRequest(
    string Name,
    AccountType Type,
    string Currency,
    DateOnly OpeningDate,
    long OpeningBalance,
    bool? IsOnBudget = null,
    string? Notes = null,
    DebtTerms? Debt = null);

/// <summary>Input for <see cref="IAccountService.UpdateAccountAsync"/>.</summary>
/// <param name="Id">Account.</param>
/// <param name="Name">New name.</param>
/// <param name="IsOnBudget">New on-budget flag (only Savings and Cash may differ from the default).</param>
/// <param name="Notes">New notes.</param>
/// <param name="Debt">New debt terms (liability types only); null leaves them unchanged, <see cref="DebtTerms.None"/> clears them.</param>
public sealed record UpdateAccountRequest(Guid Id, string Name, bool IsOnBudget, string? Notes, DebtTerms? Debt = null);
