using Keel.Application.Budget;
using Keel.Application.Goals;
using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Infrastructure.Tests.Ledger;

namespace Keel.Infrastructure.Tests.Goals;

public sealed class GoalServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Creating_a_goal_makes_a_category_in_the_goals_group_with_a_by_date_target()
    {
        await using var host = await LedgerTestHost.CreateAsync();
        await host.CheckingAsync(opening: 5_000_00);
        var brokerage = await host.AccountAsync("Brokerage", AccountType.Investment, 1_000_00);
        var goals = host.Get<IGoalService>();
        var budget = host.Get<IBudgetService>();
        var month = BudgetMonth.Of(DateOnly.FromDateTime(DateTime.Today));

        (await goals.GetGoalsAsync(month, Ct)).ShouldBeEmpty();
        var goal = await goals.CreateGoalAsync(new CreateGoalRequest("Vacation", 1_200_00, month.AddMonths(5), brokerage.Id, "Goals"), month, Ct);

        goal.Name.ShouldBe("Vacation");
        goal.GroupName.ShouldBe("Goals");
        goal.Target.ShouldBe(1_200_00);
        goal.TargetDate.ShouldBe(month.AddMonths(5));
        goal.Available.ShouldBe(0);
        goal.MonthlyNeed.ShouldBe(200_00); // 1,200 over 6 months (this month through the target month)
        goal.LinkedAccountName.ShouldBe("Brokerage");
        goal.LinkedAccountBalance.ShouldBe(1_000_00);
        (await budget.GetTargetAsync(goal.CategoryId, Ct))!.Type.ShouldBe(TargetType.SavingsBalanceByDate);

        // A second goal reuses the group; the pace is the three-month average of Assigned.
        var car = await goals.CreateGoalAsync(new CreateGoalRequest("New car", 9_000_00, month.AddMonths(24), null, "Goals"), month, Ct);
        await budget.AssignAsync(car.CategoryId, month.AddMonths(-2), 300_00, Ct);
        await budget.AssignAsync(car.CategoryId, month, 150_00, Ct);
        var list = await goals.GetGoalsAsync(month, Ct);
        list.Select(g => g.Name).ShouldBe(["Vacation", "New car"]);
        var updated = list.Single(g => g.CategoryId == car.CategoryId);
        updated.AveragePace.ShouldBe(150_00);
        updated.Available.ShouldBe(450_00);
        updated.AssignedThisMonth.ShouldBe(150_00);
        (await host.Categories.GetCategoriesAsync(false, Ct)).Where(c => c.GroupName == "Goals").Count().ShouldBe(2);
    }

    [Fact]
    public async Task Goals_are_only_by_date_savings_targets_and_links_must_be_tracking_accounts()
    {
        await using var host = await LedgerTestHost.CreateAsync();
        var checking = await host.CheckingAsync();
        var goals = host.Get<IGoalService>();
        var budget = host.Get<IBudgetService>();
        var month = BudgetMonth.Of(DateOnly.FromDateTime(DateTime.Today));
        var groceries = await host.CategoryAsync("Groceries");
        await budget.SetTargetAsync(new TargetDto(groceries, TargetType.MonthlySpending, 400_00), Ct);

        (await goals.GetGoalsAsync(month, Ct)).ShouldBeEmpty();
        await Should.ThrowAsync<ArgumentException>(() => goals.CreateGoalAsync(new CreateGoalRequest("Fund", 100_00, month, checking.Id, "Goals"), month, Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => goals.CreateGoalAsync(new CreateGoalRequest("Fund", 0, month, null, "Goals"), month, Ct));
        (await host.Categories.GetCategoriesAsync(false, Ct)).ShouldNotContain(c => c.Name == "Fund");
    }
}
