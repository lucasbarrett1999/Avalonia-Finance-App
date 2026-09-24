namespace Keel.Domain.Entities;

/// <summary>Amount assigned to a category in a month. An absent row means 0.</summary>
public class BudgetAssignment
{
    /// <summary>Category.</summary>
    public Guid CategoryId { get; set; }

    /// <summary>First day of the month.</summary>
    public DateOnly Month { get; set; }

    /// <summary>Assigned amount in minor units.</summary>
    public long Assigned { get; set; }

    /// <summary>Returns the first day of the month containing <paramref name="date"/>.</summary>
    public static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);
}
