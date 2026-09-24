namespace Keel.Application.Ledger;

/// <summary>Why a ledger operation was refused. The UI maps each code to a localized message.</summary>
public enum LedgerError
{
    /// <summary>The account does not exist.</summary>
    AccountNotFound,

    /// <summary>The account is closed; reopen it first.</summary>
    AccountClosed,

    /// <summary>An account needs a name.</summary>
    AccountNameRequired,

    /// <summary>The currency is not a three-letter ISO 4217 code.</summary>
    InvalidCurrency,

    /// <summary>Only Savings and Cash accounts may change the on-budget default (PRD 6.2).</summary>
    OnBudgetNotAllowed,

    /// <summary>The on-budget flag cannot change once an account has transactions.</summary>
    OnBudgetChangeWithTransactions,

    /// <summary>Closing needs a zero balance or an explicit "close with balance" confirmation.</summary>
    AccountHasBalance,

    /// <summary>The transaction does not exist.</summary>
    TransactionNotFound,

    /// <summary>The category does not exist.</summary>
    CategoryNotFound,

    /// <summary>A category needs a name.</summary>
    CategoryNameRequired,

    /// <summary>A transfer between an on-budget and a tracking account needs a category.</summary>
    CategoryRequiredForTransfer,

    /// <summary>A transfer needs two different accounts.</summary>
    TransferToSameAccount,

    /// <summary>A split needs at least two lines.</summary>
    SplitTooFewLines,

    /// <summary>A split line has a zero amount.</summary>
    SplitZeroLine,

    /// <summary>The split lines do not sum to the transaction amount.</summary>
    SplitSumMismatch,

    /// <summary>A transfer cannot be split.</summary>
    SplitTransfer,

    /// <summary>Reconciled transactions are locked: amount, date, account and deletion are refused.</summary>
    ReconciledLocked,

    /// <summary>Finishing a reconciliation needs a zero difference or a balance adjustment.</summary>
    ReconciliationNotBalanced,

    /// <summary>System groups and categories (Inflow, Ready to Assign, Credit Card Payments) cannot be changed this way.</summary>
    SystemCategoryProtected,

    /// <summary>The category group does not exist.</summary>
    CategoryGroupNotFound,

    /// <summary>The category has history (transactions, assignments or schedules); choose a replacement category.</summary>
    ReplacementCategoryRequired,

    /// <summary>The replacement category is the deleted one, is being deleted too, or is a system category.</summary>
    InvalidReplacementCategory,
}

/// <summary>A ledger rule was violated; nothing was written.</summary>
public sealed class LedgerValidationException : InvalidOperationException
{
    /// <summary>Creates the exception for <paramref name="error"/>.</summary>
    public LedgerValidationException(LedgerError error)
        : base($"Ledger rule violated: {error}.")
    {
        Error = error;
    }

    /// <summary>Creates the exception with a default code.</summary>
    public LedgerValidationException()
        : this(LedgerError.TransactionNotFound)
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public LedgerValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public LedgerValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The violated rule.</summary>
    public LedgerError Error { get; }
}
