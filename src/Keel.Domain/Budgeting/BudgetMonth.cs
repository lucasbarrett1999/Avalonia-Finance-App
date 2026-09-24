namespace Keel.Domain.Budgeting;

/// <summary>
/// Calendar-month helpers for the budget (PRD 6.4.6). A month is identified by its first day;
/// there is no time component and no time zone anywhere in the ledger.
/// </summary>
public static class BudgetMonth
{
    /// <summary>The first day of the month containing <paramref name="date"/>.</summary>
    public static DateOnly Of(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>The first day of the month <paramref name="months"/> after (or before) <paramref name="month"/>.</summary>
    public static DateOnly Add(DateOnly month, int months) => Of(month).AddMonths(months);

    /// <summary>A dense, ordered index of the month (year × 12 + month − 1).</summary>
    public static int Index(DateOnly date) => (date.Year * 12) + date.Month - 1;

    /// <summary>The month (first day) for a dense index produced by <see cref="Index"/>.</summary>
    public static DateOnly FromIndex(int index) => new(index / 12, (index % 12) + 1, 1);

    /// <summary>Number of months from <paramref name="from"/> to <paramref name="to"/> (0 when equal, negative when earlier).</summary>
    public static int Between(DateOnly from, DateOnly to) => Index(to) - Index(from);

    /// <summary>The first day after the month containing <paramref name="date"/> (exclusive upper bound for queries).</summary>
    public static DateOnly NextOf(DateOnly date) => Of(date).AddMonths(1);
}
