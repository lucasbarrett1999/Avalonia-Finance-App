using Keel.Domain.Entities;

namespace Keel.Domain.Budgeting;

/// <summary>Where a category stands against its target in one month (F-BUD-4).</summary>
/// <param name="Type">Target type.</param>
/// <param name="Amount">Target amount.</param>
/// <param name="TargetDate">Target date (savings balance by date).</param>
/// <param name="NeededThisMonth">What the target asks to be assigned this month in total.</param>
/// <param name="Underfunded">How much more must be assigned this month ("Underfunded by"); 0 when met.</param>
/// <param name="MonthlyNeed">The per-month amount the target implies (for by-date targets: the remaining balance spread over the remaining months).</param>
/// <param name="MonthsRemaining">By-date targets: months from this month through the target month (at least 1).</param>
/// <param name="IsComplete">By-date targets: Available has reached the target amount.</param>
public sealed record TargetStatus(
    TargetType Type,
    long Amount,
    DateOnly? TargetDate,
    long NeededThisMonth,
    long Underfunded,
    long MonthlyNeed,
    int? MonthsRemaining,
    bool IsComplete)
{
    /// <summary>True when nothing more needs to be assigned this month.</summary>
    public bool IsFunded => Underfunded == 0;
}

/// <summary>
/// Target math (F-BUD-4). Interpretations are recorded in
/// <c>docs/decisions/0006-targets-and-quick-assign.md</c>.
/// </summary>
public static class TargetCalculator
{
    /// <summary>Computes the status of <paramref name="target"/> for the month of <paramref name="cell"/>.</summary>
    public static TargetStatus Compute(Target target, CategoryMonthResult cell)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(cell);
        var amount = target.Amount;

        switch (target.Type)
        {
            case TargetType.MonthlySetAside:
            case TargetType.DebtPayment:
                // Assign X every month (debt: toward the linked account).
                return new TargetStatus(target.Type, amount, target.TargetDate, amount, Math.Max(0, amount - cell.Assigned), amount, null, false);

            case TargetType.MonthlySpending:
                {
                    // Refill to X: what is carried in counts, this month's spending does not raise the need.
                    var needed = Math.Max(0, amount - cell.Carry);
                    var underfunded = Math.Max(0, amount - (cell.Carry + cell.Assigned));
                    return new TargetStatus(target.Type, amount, target.TargetDate, needed, underfunded, amount, null, false);
                }

            case TargetType.SavingsBalanceByDate:
                {
                    // Reach X by D: spread what is missing before this month's assignment over the months
                    // left (this month through D's month), rounding up so the target is met on time.
                    var months = target.TargetDate is { } date ? Math.Max(1, BudgetMonth.Between(cell.Month, date) + 1) : 1;
                    var before = cell.Available - cell.Assigned;
                    var missing = Math.Max(0, amount - before);
                    var needed = (missing + months - 1) / months;
                    var underfunded = Math.Max(0, needed - cell.Assigned);
                    return new TargetStatus(target.Type, amount, target.TargetDate, needed, underfunded, needed, months, cell.Available >= amount);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target.Type, "Unknown target type.");
        }
    }
}

/// <summary>Values for the quick-assign actions of one category and month (F-BUD-5); each is a value for Assigned.</summary>
/// <param name="AssignedLastMonth">Assigned in the previous month.</param>
/// <param name="SpentLastMonth">What was spent in the previous month (−Activity, floored at 0).</param>
/// <param name="AverageAssigned">Average Assigned over the previous 3 months (banker's rounding).</param>
/// <param name="AverageSpent">Average spent over the previous 3 months (banker's rounding, floored at 0).</param>
/// <param name="FundTarget">Assigned plus Underfunded, or null without a target.</param>
/// <param name="ResetToZero">Always 0.</param>
public sealed record QuickAssignValues(
    long AssignedLastMonth,
    long SpentLastMonth,
    long AverageAssigned,
    long AverageSpent,
    long? FundTarget,
    long ResetToZero);

/// <summary>Quick-assign math (F-BUD-5).</summary>
public static class QuickAssign
{
    /// <summary>
    /// Computes the quick-assign values from a snapshot that covers the three months before
    /// <paramref name="month"/> and the month itself.
    /// </summary>
    public static QuickAssignValues Compute(BudgetSnapshot snapshot, Guid categoryId, DateOnly month, TargetStatus? target)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var current = snapshot.Cell(categoryId, month);
        var previous = Enumerable.Range(1, 3).Select(i => snapshot.Cell(categoryId, BudgetMonth.Add(month, -i))).ToList();

        return new QuickAssignValues(
            AssignedLastMonth: previous[0].Assigned,
            SpentLastMonth: Math.Max(0, -previous[0].Activity),
            AverageAssigned: DivideHalfEven(previous.Sum(c => c.Assigned), 3),
            AverageSpent: Math.Max(0, DivideHalfEven(-previous.Sum(c => c.Activity), 3)),
            FundTarget: target is null ? null : current.Assigned + target.Underfunded,
            ResetToZero: 0);
    }

    /// <summary>Integer division rounded half to even (banker's rounding, PRD 6.1).</summary>
    public static long DivideHalfEven(long numerator, long denominator)
    {
        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), denominator, "Denominator must be positive.");
        }

        var quotient = Math.DivRem(numerator, denominator, out var remainder);
        var twice = Math.Abs(remainder) * 2;
        if (twice > denominator || (twice == denominator && quotient % 2 != 0))
        {
            quotient += Math.Sign(numerator);
        }

        return quotient;
    }
}
