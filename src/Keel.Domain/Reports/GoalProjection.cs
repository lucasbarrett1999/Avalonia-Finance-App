using Keel.Domain.Budgeting;

namespace Keel.Domain.Reports;

/// <summary>Where a savings goal gets to at a monthly pace (F-GOAL-1).</summary>
/// <param name="IsComplete">The balance already reaches the target.</param>
/// <param name="Remaining">Amount still to save (0 when complete).</param>
/// <param name="MonthsToGo">Months of saving at the pace still needed; null when the pace is not positive.</param>
/// <param name="CompletionMonth">First day of the month the goal is reached; null when it never is at this pace.</param>
public sealed record GoalProjectionResult(bool IsComplete, long Remaining, int? MonthsToGo, DateOnly? CompletionMonth)
{
    /// <summary>Whether the goal is reached by the end of <paramref name="targetMonth"/>'s month.</summary>
    public bool IsOnTrackFor(DateOnly? targetMonth) =>
        IsComplete || (CompletionMonth is { } done && (targetMonth is null || done <= BudgetMonth.Of(targetMonth.Value)));
}

/// <summary>Goal projection math (F-GOAL-1): pure, integer minor units.</summary>
public static class GoalProjection
{
    /// <summary>Number of months the "current pace" averages (the three months ending this month).</summary>
    public const int PaceMonths = 3;

    /// <summary>
    /// Projects a goal: the balance (<paramref name="available"/>, this month's assignment included)
    /// grows by <paramref name="monthlyContribution"/> each following month.
    /// </summary>
    public static GoalProjectionResult Project(long available, long target, long monthlyContribution, DateOnly currentMonth)
    {
        var remaining = target - available;
        if (remaining <= 0)
        {
            return new GoalProjectionResult(true, 0, 0, BudgetMonth.Of(currentMonth));
        }

        if (monthlyContribution <= 0)
        {
            return new GoalProjectionResult(false, remaining, null, null);
        }

        var months = (int)Math.Min(1200, (remaining + monthlyContribution - 1) / monthlyContribution);
        return new GoalProjectionResult(false, remaining, months, BudgetMonth.Of(currentMonth).AddMonths(months));
    }

    /// <summary>
    /// The current pace: the average of the assigned amounts (one per month, missing months are 0),
    /// rounded half to even, as the quick-assign averages are (ADR 0008).
    /// </summary>
    public static long AveragePace(IReadOnlyCollection<long> assignedPerMonth)
    {
        ArgumentNullException.ThrowIfNull(assignedPerMonth);
        if (assignedPerMonth.Count == 0)
        {
            return 0;
        }

        var total = assignedPerMonth.Aggregate(0L, (sum, a) => checked(sum + a));
        return (long)Math.Round((decimal)total / assignedPerMonth.Count, MidpointRounding.ToEven);
    }
}
