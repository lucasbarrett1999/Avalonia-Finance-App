using Keel.Domain;

namespace Keel.Application.Reports;

/// <summary>
/// Report queries (F-REP-1..3). Every number is aggregated in SQL from the ledger (principle 1);
/// no transaction row is loaded into memory and every call runs on the thread pool.
/// </summary>
public interface IReportService
{
    /// <summary>Spending by group and category for a range, with the previous period (F-REP-1).</summary>
    Task<SpendingReport> GetSpendingAsync(ReportQuery query, CancellationToken ct);

    /// <summary>Income, expense and net per month (F-REP-2).</summary>
    Task<IncomeExpenseReport> GetIncomeExpenseAsync(ReportQuery query, CancellationToken ct);

    /// <summary>Monthly end-of-month net worth with a per-account breakdown (F-REP-3, F-ACC-7).</summary>
    Task<NetWorthReport> GetNetWorthAsync(ReportQuery query, CancellationToken ct);

    /// <summary>
    /// Age of money (F-REP-5, ADR 0094) at the end of every month from <paramref name="from"/> to
    /// <paramref name="to"/> (the last point is today when the range reaches it). One <c>GROUP BY</c>
    /// day over on-budget cash accounts feeds <see cref="Keel.Domain.Reports.AgeOfMoney"/>.
    /// </summary>
    Task<AgeOfMoneyReport> GetAgeOfMoneyAsync(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>
    /// Budget health for the month of <paramref name="month"/> (F-REP-5, ADR 0094): age of money over
    /// the twelve months ending with it, months ahead, targets funded and overspent categories. Budget
    /// numbers come from the budget service (and so from <c>BudgetCalculator</c>); spending from SQL.
    /// </summary>
    Task<BudgetHealthReport> GetBudgetHealthAsync(DateOnly month, CancellationToken ct);
}

/// <summary>Age of money over time (F-REP-5).</summary>
/// <param name="Points">Month-end points, oldest first (the current month's point is today).</param>
public sealed record AgeOfMoneyReport(IReadOnlyList<Keel.Domain.Reports.AgeOfMoneyPoint> Points)
{
    /// <summary>The latest point, or null for an empty range.</summary>
    public Keel.Domain.Reports.AgeOfMoneyPoint? Latest => Points.Count == 0 ? null : Points[^1];
}

/// <summary>Budget health for a month (F-REP-5).</summary>
/// <param name="Currency">Budget currency.</param>
/// <param name="Month">The month (first day).</param>
/// <param name="AsOf">The date the numbers describe: the month's last day, or today for the current month.</param>
/// <param name="AgeOfMoney">Age of money at the month ends of the twelve months ending with <paramref name="Month"/>.</param>
/// <param name="MonthsAhead">Months-ahead metric.</param>
/// <param name="Targets">Targets funded this month.</param>
/// <param name="Overspent">Overspent categories this month, most overspent first.</param>
public sealed record BudgetHealthReport(
    string Currency,
    DateOnly Month,
    DateOnly AsOf,
    AgeOfMoneyReport AgeOfMoney,
    MonthsAheadMetric MonthsAhead,
    TargetsFundedMetric Targets,
    IReadOnlyList<OverspentCategory> Overspent)
{
    /// <summary>Overspent categories.</summary>
    public int OverspentCount => Overspent.Count;

    /// <summary>Of which some cash was overspent (red, reduces next month's Ready to Assign).</summary>
    public int CashOverspentCount => Overspent.Count(o => o.IsCash);
}

/// <summary>
/// How many months of typical spending the money already in the budget covers: <see cref="Buffer"/> ÷
/// <see cref="AverageSpending"/>, one decimal, rounded down (ADR 0094).
/// </summary>
/// <param name="Buffer">Ready to Assign plus every positive Available outside credit card payment categories.</param>
/// <param name="ReadyToAssign">Ready to Assign (part of the buffer; may be negative).</param>
/// <param name="AverageSpending">Average monthly spending of <paramref name="SpendingFrom"/>..<paramref name="SpendingTo"/> (as the Spending report counts it).</param>
/// <param name="SpendingFrom">First month averaged.</param>
/// <param name="SpendingTo">Last month averaged.</param>
/// <param name="Tenths">Months ahead in tenths (21 = 2.1 months), or null without spending to compare with.</param>
public sealed record MonthsAheadMetric(long Buffer, long ReadyToAssign, long AverageSpending, DateOnly SpendingFrom, DateOnly SpendingTo, int? Tenths);

/// <summary>Targets funded this month (F-BUD-4 targets).</summary>
/// <param name="Count">Visible categories with a target.</param>
/// <param name="Funded">Of which nothing more is needed this month.</param>
/// <param name="Needed">Σ what the targets ask to assign this month.</param>
/// <param name="Underfunded">Σ still missing this month.</param>
public sealed record TargetsFundedMetric(int Count, int Funded, long Needed, long Underfunded)
{
    /// <summary>Share of the money needed this month that is assigned, whole percent (half to even); 100 when nothing is needed; null without targets.</summary>
    public int? Percent => Count == 0 ? null
        : Needed <= 0 ? 100
        : (int)Math.Round((decimal)(Needed - Underfunded) * 100 / Needed, MidpointRounding.ToEven);
}

/// <summary>An overspent category.</summary>
/// <param name="CategoryId">Category.</param>
/// <param name="Name">Name.</param>
/// <param name="GroupName">Group name.</param>
/// <param name="Available">Available (negative).</param>
/// <param name="IsCash">Cash overspending (red) rather than credit only (yellow).</param>
public sealed record OverspentCategory(Guid CategoryId, string Name, string GroupName, long Available, bool IsCash);

/// <summary>What a report covers (the shared report toolbar, PRD 9.8).</summary>
/// <param name="From">First date, inclusive.</param>
/// <param name="To">Last date, inclusive.</param>
/// <param name="AccountIds">Accounts to include; null or empty means every account.</param>
/// <param name="IncludeTransfers">
/// Count categorized transfers (on-budget to tracking) as spending or income. Uncategorized
/// transfers between accounts are never spending or income. Ignored by net worth.
/// </param>
/// <param name="IncludeTracking">Include tracking (off-budget) accounts.</param>
public sealed record ReportQuery(
    DateOnly From,
    DateOnly To,
    IReadOnlyCollection<Guid>? AccountIds = null,
    bool IncludeTransfers = true,
    bool IncludeTracking = false);

/// <summary>Spending (F-REP-1): outflows minus refunds, as positive numbers.</summary>
/// <param name="Currency">Budget currency.</param>
/// <param name="From">First date.</param>
/// <param name="To">Last date.</param>
/// <param name="PreviousFrom">First date of the comparison period.</param>
/// <param name="PreviousTo">Last date of the comparison period.</param>
/// <param name="Total">Σ spending of every group.</param>
/// <param name="PreviousTotal">Σ spending in the comparison period.</param>
/// <param name="Groups">Groups with any spending in either period, largest first.</param>
public sealed record SpendingReport(
    string Currency,
    DateOnly From,
    DateOnly To,
    DateOnly PreviousFrom,
    DateOnly PreviousTo,
    long Total,
    long PreviousTotal,
    IReadOnlyList<SpendingGroup> Groups);

/// <summary>A category group in the spending report.</summary>
/// <param name="GroupId">Group, or null for uncategorized activity.</param>
/// <param name="Name">Group name, or null for uncategorized activity.</param>
/// <param name="Amount">Spending in the period (negative when refunds exceed spending).</param>
/// <param name="PreviousAmount">Spending in the comparison period.</param>
/// <param name="Categories">Categories with any spending in either period, largest first.</param>
public sealed record SpendingGroup(Guid? GroupId, string? Name, long Amount, long PreviousAmount, IReadOnlyList<SpendingCategory> Categories)
{
    /// <summary>Whether this is the bucket of rows without a category.</summary>
    public bool IsUncategorized => GroupId is null;
}

/// <summary>A category in the spending report.</summary>
/// <param name="CategoryId">Category, or null for uncategorized activity.</param>
/// <param name="Name">Category name, or null for uncategorized activity.</param>
/// <param name="Amount">Spending in the period.</param>
/// <param name="PreviousAmount">Spending in the comparison period.</param>
public sealed record SpendingCategory(Guid? CategoryId, string? Name, long Amount, long PreviousAmount);

/// <summary>Income versus expense (F-REP-2).</summary>
/// <param name="Currency">Budget currency.</param>
/// <param name="From">First date.</param>
/// <param name="To">Last date.</param>
/// <param name="Months">One entry per month intersecting the range, empty months included.</param>
public sealed record IncomeExpenseReport(string Currency, DateOnly From, DateOnly To, IReadOnlyList<IncomeExpenseMonth> Months)
{
    /// <summary>Σ income.</summary>
    public long TotalIncome => Months.Sum(m => m.Income);

    /// <summary>Σ expense.</summary>
    public long TotalExpense => Months.Sum(m => m.Expense);

    /// <summary>Σ net.</summary>
    public long TotalNet => Months.Sum(m => m.Net);
}

/// <summary>One month of income versus expense.</summary>
/// <param name="Month">First day of the month.</param>
/// <param name="Income">Money categorized to Inflow (and uncategorized inflows).</param>
/// <param name="Expense">Spending (outflows minus refunds) as a positive number.</param>
public sealed record IncomeExpenseMonth(DateOnly Month, long Income, long Expense)
{
    /// <summary>Income minus expense.</summary>
    public long Net => Income - Expense;
}

/// <summary>Net worth over time (F-REP-3).</summary>
/// <param name="Currency">Budget currency.</param>
/// <param name="Points">Month-end points (the last one is at most the range end and today).</param>
/// <param name="Accounts">Per-account balances aligned with <see cref="Points"/>, in sidebar order.</param>
public sealed record NetWorthReport(string Currency, IReadOnlyList<NetWorthPoint> Points, IReadOnlyList<NetWorthAccount> Accounts);

/// <summary>Net worth at a point.</summary>
/// <param name="Date">Point date.</param>
/// <param name="Assets">Σ balances of asset accounts.</param>
/// <param name="Liabilities">Σ money owed on liability accounts, as a positive number.</param>
public sealed record NetWorthPoint(DateOnly Date, long Assets, long Liabilities)
{
    /// <summary>Assets minus liabilities.</summary>
    public long NetWorth => Assets - Liabilities;
}

/// <summary>One account's balances at the report points.</summary>
/// <param name="AccountId">Account.</param>
/// <param name="Name">Account name.</param>
/// <param name="Type">Account type.</param>
/// <param name="IsOnBudget">On-budget flag.</param>
/// <param name="IsClosed">Closed accounts stay in reports (F-ACC-1).</param>
/// <param name="UsesSnapshots">Balances come from balance snapshots (tracking account with snapshots).</param>
/// <param name="Balances">Signed balances (liabilities negative) at each point.</param>
public sealed record NetWorthAccount(Guid AccountId, string Name, AccountType Type, bool IsOnBudget, bool IsClosed, bool UsesSnapshots, IReadOnlyList<long> Balances)
{
    /// <summary>Whether the account is a liability.</summary>
    public bool IsLiability => AccountTypeInfo.IsLiability(Type);
}
