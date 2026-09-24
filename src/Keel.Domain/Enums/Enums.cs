namespace Keel.Domain;

/// <summary>Account types (F-ACC-1). Classification rules live in <see cref="AccountTypeInfo"/>.</summary>
public enum AccountType
{
    /// <summary>Checking account (on budget, cash-like).</summary>
    Checking,

    /// <summary>Savings account (on budget by default, cash-like).</summary>
    Savings,

    /// <summary>Physical cash (on budget by default, cash-like).</summary>
    Cash,

    /// <summary>Credit card (on budget, liability).</summary>
    CreditCard,

    /// <summary>Revolving line of credit (on budget, liability).</summary>
    LineOfCredit,

    /// <summary>Loan or mortgage (tracking, liability).</summary>
    Loan,

    /// <summary>Investment or brokerage account (tracking, asset).</summary>
    Investment,

    /// <summary>Any other asset such as property or a vehicle (tracking).</summary>
    OtherAsset,

    /// <summary>Any other liability (tracking).</summary>
    OtherLiability,
}

/// <summary>Cleared state of a transaction.</summary>
public enum TransactionStatus
{
    /// <summary>Not yet cleared by the bank.</summary>
    Uncleared,

    /// <summary>Cleared by the bank.</summary>
    Cleared,

    /// <summary>Locked by a completed reconciliation.</summary>
    Reconciled,
}

/// <summary>Where a transaction came from. Every source goes through the same import pipeline.</summary>
public enum TransactionSource
{
    /// <summary>Entered by the user.</summary>
    Manual,

    /// <summary>Imported from a CSV, OFX, QFX, or QIF file.</summary>
    File,

    /// <summary>Synced from a bank data provider.</summary>
    Provider,

    /// <summary>Created by the app, e.g. a starting-balance transaction.</summary>
    System,

    /// <summary>Entered from a scheduled transaction.</summary>
    Scheduled,
}

/// <summary>Bank data providers (F-TXN-3).</summary>
public enum SyncProvider
{
    /// <summary>Plaid.</summary>
    Plaid,

    /// <summary>SimpleFIN Bridge.</summary>
    SimpleFin,
}

/// <summary>Health of a sync connection.</summary>
public enum SyncStatus
{
    /// <summary>Syncing normally.</summary>
    Ok,

    /// <summary>The institution requires the user to log in again.</summary>
    NeedsReauth,

    /// <summary>The last sync failed.</summary>
    Error,

    /// <summary>Sync is turned off for this connection.</summary>
    Disabled,
}

/// <summary>Category target types (F-BUD-4).</summary>
public enum TargetType
{
    /// <summary>Assign a fixed amount every month.</summary>
    MonthlySetAside,

    /// <summary>Refill to a fixed Available amount each month.</summary>
    MonthlySpending,

    /// <summary>Reach a total balance by a date.</summary>
    SavingsBalanceByDate,

    /// <summary>Pay a fixed amount per month toward a linked debt account.</summary>
    DebtPayment,
}

/// <summary>Flex-mode tag for a category (F-BUD-6).</summary>
public enum FlexKind
{
    /// <summary>Not tagged.</summary>
    Unset,

    /// <summary>Fixed monthly cost.</summary>
    Fixed,

    /// <summary>Non-monthly cost set aside monthly.</summary>
    NonMonthly,

    /// <summary>Flexible spending.</summary>
    Flex,
}

/// <summary>Detected or configured recurrence cadences (F-REC-1).</summary>
public enum RecurrenceCadence
{
    /// <summary>Every 7 days.</summary>
    Weekly,

    /// <summary>Every 14 days.</summary>
    Biweekly,

    /// <summary>Twice a month.</summary>
    Semimonthly,

    /// <summary>Once a month.</summary>
    Monthly,

    /// <summary>Every three months.</summary>
    Quarterly,

    /// <summary>Once a year.</summary>
    Yearly,
}

/// <summary>Lifecycle of a recurring item.</summary>
public enum RecurringStatus
{
    /// <summary>Expected to recur.</summary>
    Active,

    /// <summary>Paused by the user.</summary>
    Paused,

    /// <summary>No longer recurring.</summary>
    Ended,

    /// <summary>Dismissed by the user; detection will not recreate it.</summary>
    Dismissed,

    /// <summary>Found by recurring detection and not yet confirmed by the user (F-REC-1, ADR 0031).</summary>
    Detected,
}

/// <summary>Notification-center alert kinds (F-REC-3).</summary>
public enum AlertKind
{
    /// <summary>A recurring amount increased by more than 5%.</summary>
    PriceIncrease,

    /// <summary>An expected recurring item is 3 or more days past due.</summary>
    MissingExpected,

    /// <summary>A new recurring item was detected.</summary>
    NewRecurring,

    /// <summary>The first real charge after a $0 or trial charge from the same payee.</summary>
    TrialConversion,
}

/// <summary>Origin of a balance snapshot (F-ACC-7).</summary>
public enum BalanceSource
{
    /// <summary>Entered by the user.</summary>
    Manual,

    /// <summary>Reported by a bank data provider.</summary>
    Provider,
}

/// <summary>Kinds of audit events that power undo and the activity log.</summary>
public enum AuditEventKind
{
    /// <summary>An entity was created.</summary>
    Created,

    /// <summary>An entity was changed.</summary>
    Updated,

    /// <summary>An entity was deleted (soft or hard).</summary>
    Deleted,
}

/// <summary>Sidebar and report grouping of accounts (PRD 9.1).</summary>
public enum AccountGroup
{
    /// <summary>On-budget cash accounts.</summary>
    Cash,

    /// <summary>On-budget credit accounts.</summary>
    Credit,

    /// <summary>Off-budget tracking accounts.</summary>
    Tracking,
}
