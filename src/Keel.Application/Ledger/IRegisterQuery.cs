using Keel.Domain;

namespace Keel.Application.Ledger;

/// <summary>
/// The account register as a paged, database-backed source (F-ACC-2, PRD 7.3). Only the
/// requested page is read; filtering, sorting, search and the running balance are all computed
/// in SQL, so 100k-row registers never load into memory.
/// </summary>
public interface IRegisterQuery
{
    /// <summary>Rows per page.</summary>
    const int PageSize = 200;

    /// <summary>Number of rows matching <paramref name="filter"/>.</summary>
    Task<int> CountAsync(RegisterFilter filter, CancellationToken ct);

    /// <summary>One page of rows in the requested order.</summary>
    Task<IReadOnlyList<RegisterRow>> GetPageAsync(RegisterFilter filter, RegisterSort sort, int skip, int take, CancellationToken ct);

    /// <summary>Position of a transaction in the filtered, sorted register, or -1 when not visible.</summary>
    Task<int> IndexOfAsync(RegisterFilter filter, RegisterSort sort, Guid transactionId, CancellationToken ct);

    /// <summary>Header balances for one account, or for all accounts when <paramref name="accountId"/> is null.</summary>
    Task<RegisterSummary> GetSummaryAsync(Guid? accountId, CancellationToken ct);
}

/// <summary>Register filter (PRD 9.4 filter bar).</summary>
/// <param name="AccountId">One account, or null for the All Accounts register.</param>
/// <param name="From">First date, inclusive.</param>
/// <param name="To">Last date, inclusive.</param>
/// <param name="Statuses">Statuses to show; null or empty shows all.</param>
/// <param name="CategoryId">Only rows (or splits) in this category.</param>
/// <param name="UnapprovedOnly">Only unapproved rows.</param>
/// <param name="Search">Search text in the F-TXN-7 query syntax.</param>
/// <param name="TagId">Only rows with this tag (F-TXN-8).</param>
public sealed record RegisterFilter(
    Guid? AccountId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    IReadOnlyCollection<TransactionStatus>? Statuses = null,
    Guid? CategoryId = null,
    bool UnapprovedOnly = false,
    string? Search = null,
    Guid? TagId = null);

/// <summary>Sortable register columns.</summary>
public enum RegisterSortColumn
{
    /// <summary>Ledger order: date, then entry order.</summary>
    Date,

    /// <summary>Account name.</summary>
    Account,

    /// <summary>Payee (or transfer account) name.</summary>
    Payee,

    /// <summary>Category name.</summary>
    Category,

    /// <summary>Memo.</summary>
    Memo,

    /// <summary>Signed amount.</summary>
    Amount,

    /// <summary>Cleared status.</summary>
    Status,
}

/// <summary>Register order.</summary>
/// <param name="Column">Primary column.</param>
/// <param name="Descending">Direction.</param>
public sealed record RegisterSort(RegisterSortColumn Column = RegisterSortColumn.Date, bool Descending = true)
{
    /// <summary>Newest first (the default register order).</summary>
    public static RegisterSort Default { get; } = new();
}

/// <summary>One register row.</summary>
/// <param name="RunningBalance">Account balance after this row in ledger order (All Accounts: sum over all accounts).</param>
public sealed record RegisterRow(
    Guid Id,
    Guid AccountId,
    string AccountName,
    string Currency,
    DateOnly Date,
    Guid? PayeeId,
    string Payee,
    Guid? CategoryId,
    string? CategoryName,
    string? Memo,
    long Amount,
    TransactionStatus Status,
    bool IsApproved,
    TransactionSource Source,
    Guid? TransferAccountId,
    string? TransferAccountName,
    Guid? TransferPairId,
    long RunningBalance,
    IReadOnlyList<RegisterSplit> Splits)
{
    /// <summary>Whether the row is one side of a transfer.</summary>
    public bool IsTransfer => TransferAccountId is not null;

    /// <summary>Whether the row is split.</summary>
    public bool IsSplit => Splits.Count > 0;

    /// <summary>Tag names by name (F-TXN-8).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Number of attached files (F-TXN-8).</summary>
    public int AttachmentCount { get; init; }
}

/// <summary>A split line of a register row.</summary>
public sealed record RegisterSplit(Guid Id, Guid? CategoryId, string? CategoryName, string? Memo, long Amount);

/// <summary>Register header balances (PRD 9.4) in minor units.</summary>
/// <param name="Currency">Currency of the balances.</param>
/// <param name="Ledger">All non-deleted transactions.</param>
/// <param name="Cleared">Cleared and reconciled transactions.</param>
/// <param name="ReportedBalance">Provider-reported balance, when linked.</param>
/// <param name="ReportedAt">When the provider reported it (UTC).</param>
/// <param name="TransactionCount">Number of non-deleted transactions.</param>
/// <param name="UnapprovedCount">Number of unapproved transactions.</param>
public sealed record RegisterSummary(
    string Currency,
    long Ledger,
    long Cleared,
    long? ReportedBalance,
    DateTime? ReportedAt,
    int TransactionCount,
    int UnapprovedCount)
{
    /// <summary>Uncleared balance.</summary>
    public long Uncleared => Ledger - Cleared;
}
