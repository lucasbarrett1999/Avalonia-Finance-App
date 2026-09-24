namespace Keel.Application.Goals;

/// <summary>
/// Goals (F-GOAL-1): a goal is a category with a savings-balance-by-date target and an optional
/// linked tracking account. There is no goal entity; this service reads and writes categories and
/// targets through the category and budget services.
/// </summary>
public interface IGoalService
{
    /// <summary>Every visible category with a savings-balance-by-date target, as of <paramref name="month"/>.</summary>
    Task<IReadOnlyList<GoalDto>> GetGoalsAsync(DateOnly month, CancellationToken ct);

    /// <summary>Creates the category (in <see cref="CreateGoalRequest.GroupName"/>, created on demand) and its target.</summary>
    Task<GoalDto> CreateGoalAsync(CreateGoalRequest request, DateOnly month, CancellationToken ct);
}

/// <summary>Input for <see cref="IGoalService.CreateGoalAsync"/>.</summary>
/// <param name="Name">Category name.</param>
/// <param name="Amount">Target balance in minor units (&gt; 0).</param>
/// <param name="TargetDate">Date to reach it by.</param>
/// <param name="LinkedAccountId">Optional tracking account the money is kept in.</param>
/// <param name="GroupName">Category group for new goals.</param>
public sealed record CreateGoalRequest(string Name, long Amount, DateOnly TargetDate, Guid? LinkedAccountId, string GroupName);

/// <summary>A goal and its progress in a month.</summary>
/// <param name="CategoryId">Category.</param>
/// <param name="Name">Category name.</param>
/// <param name="GroupName">Group name.</param>
/// <param name="Currency">Budget currency.</param>
/// <param name="Target">Target balance.</param>
/// <param name="TargetDate">Target date.</param>
/// <param name="Available">Available this month (the saved balance).</param>
/// <param name="AssignedThisMonth">Assigned this month.</param>
/// <param name="MonthlyNeed">The remaining balance spread over the months left (F-BUD-4 monthly need).</param>
/// <param name="Underfunded">Still to assign this month to stay on schedule.</param>
/// <param name="AveragePace">Average assigned over the last three months (this month included).</param>
/// <param name="LinkedAccountId">Linked account.</param>
/// <param name="LinkedAccountName">Linked account name.</param>
/// <param name="LinkedAccountBalance">Linked account ledger balance.</param>
public sealed record GoalDto(
    Guid CategoryId,
    string Name,
    string GroupName,
    string Currency,
    long Target,
    DateOnly TargetDate,
    long Available,
    long AssignedThisMonth,
    long MonthlyNeed,
    long Underfunded,
    long AveragePace,
    Guid? LinkedAccountId,
    string? LinkedAccountName,
    long? LinkedAccountBalance);
