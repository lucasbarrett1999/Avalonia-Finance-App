using Keel.Domain.Budgeting;
using Keel.Domain.Entities;
using static Keel.Domain.Tests.Budgeting.BudgetBuilder;

namespace Keel.Domain.Tests.Budgeting;

/// <summary>Invariants on generated inputs, cross-checks against the naive reference, and input validation.</summary>
public class BudgetCalculatorTests
{
    public static TheoryData<int> Seeds => [1, 2, 3, 42, 2026];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Matches_the_naive_reference_transcription_of_6_4(int seed)
    {
        var input = BudgetInputGenerator.Generate(months: 5, categories: 6, accounts: 5, seed, density: 0.6);
        var from = new DateOnly(2024, 1, 1);
        var snapshot = BudgetCalculator.Compute(input, from, from.AddMonths(5)); // one empty month at the end
        var reference = new ReferenceBudget(input, from);

        foreach (var month in snapshot.Months)
        {
            month.ReadyToAssign.ShouldBe(reference.ReadyToAssign(month.Month), $"RTA {month.Month}");
            foreach (var cell in month.Categories)
            {
                var id = cell.CategoryId;
                cell.Carry.ShouldBe(reference.Carry(id, month.Month));
                cell.Assigned.ShouldBe(reference.Assigned(id, month.Month));
                cell.Activity.ShouldBe(reference.Activity(id, month.Month));
                cell.Available.ShouldBe(reference.Available(id, month.Month));
                cell.CashOverspent.ShouldBe(reference.CashOverspent(id, month.Month));
                cell.CreditOverspent.ShouldBe(reference.CreditOverspent(id, month.Month));
                if (cell.Kind == BudgetCategoryKind.Regular)
                {
                    var expected = reference.CoveredByCard(id, month.Month);
                    cell.CoveredByCard.ToDictionary(x => x.AccountId, x => x.Amount).ShouldBe(expected, ignoreOrder: true);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Invariants_hold_for_every_cell(int seed)
    {
        var input = BudgetInputGenerator.Generate(months: 12, categories: 25, accounts: 7, seed, density: 0.5);
        var snapshot = BudgetCalculator.Compute(input, new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 1));

        foreach (var month in snapshot.Months)
        {
            var rta = snapshot.ExplainReadyToAssign(month.Month);
            rta.Lines.Where(l => l.IsTerm).Sum(l => l.Amount).ShouldBe(month.ReadyToAssign);

            foreach (var cell in month.Categories)
            {
                // Explanation terms add up to Available.
                var explanation = snapshot.Explain(cell.CategoryId, month.Month);
                explanation.Lines.Where(l => l.IsTerm).Sum(l => l.Amount).ShouldBe(cell.Available);

                cell.Available.ShouldBe(cell.RawAvailable);
                cell.RawAvailable.ShouldBe(cell.Carry + cell.Assigned + cell.Activity);
                cell.Carry.ShouldBe(Math.Max(0, cell.PreviousAvailable));
                (cell.CashOverspent + cell.CreditOverspent).ShouldBe(Math.Min(0, cell.Available));
                cell.CashOverspent.ShouldBeLessThanOrEqualTo(0);
                cell.CreditOverspent.ShouldBeLessThanOrEqualTo(0);
                cell.CreditOverspent.ShouldBeGreaterThanOrEqualTo(-cell.CreditSpending);

                if (cell.Kind == BudgetCategoryKind.Regular)
                {
                    cell.ActivityByAccount.Sum(a => a.Amount).ShouldBe(cell.Activity);
                    cell.CardSpendByCard.Sum(a => a.Amount).ShouldBe(cell.CreditSpending);
                    cell.Covered.ShouldBe(cell.CreditSpending + cell.CreditOverspent);      // Σ_K Covered = Magnitude − Uncovered
                    for (var i = 0; i < cell.CoveredByCard.Count; i++)
                    {
                        cell.CoveredByCard[i].Amount.ShouldBeInRange(0, cell.CardSpendByCard[i].Amount);
                    }
                }
                else
                {
                    cell.Activity.ShouldBe(cell.Covered - cell.Payments);
                    cell.CoveredFromCategories.Sum(x => x.Amount).ShouldBe(cell.Covered);
                    cell.PaymentsByAccount.Sum(x => x.Amount).ShouldBe(cell.Payments);
                    cell.CreditOverspent.ShouldBe(0);
                }
            }

            // Covered money leaves spending categories and arrives in payment categories exactly.
            month.Categories.Where(c => c.Kind == BudgetCategoryKind.Regular).Sum(c => c.Covered)
                .ShouldBe(month.Categories.Where(c => c.Kind == BudgetCategoryKind.CreditCardPayment).Sum(c => c.Covered));

            month.CashOverspentThisMonth.ShouldBe(month.Categories.Sum(c => c.CashOverspent));
            month.TotalAssigned.ShouldBe(month.Categories.Sum(c => c.Assigned));
            month.TotalActivity.ShouldBe(month.Categories.Sum(c => c.Activity));
            month.TotalAvailable.ShouldBe(month.Categories.Sum(c => c.Available));
            foreach (var group in month.Groups)
            {
                var visible = month.Categories.Where(c => group.CategoryIds.Contains(c.CategoryId) && c.IsVisible).ToList();
                group.Available.ShouldBe(visible.Sum(c => c.Available));
                group.Assigned.ShouldBe(visible.Sum(c => c.Assigned));
                group.Activity.ShouldBe(visible.Sum(c => c.Activity));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Results_do_not_depend_on_the_requested_range_or_input_order(int seed)
    {
        var input = BudgetInputGenerator.Generate(months: 10, categories: 12, accounts: 6, seed, density: 0.7);
        var full = BudgetCalculator.Compute(input, new DateOnly(2023, 11, 1), new DateOnly(2024, 12, 1));
        var shuffled = input with
        {
            Activity = [.. input.Activity.OrderBy(_ => Random.Shared.Next())],
            Assignments = [.. input.Assignments.AsEnumerable().Reverse()],
            CardTransfers = [.. input.CardTransfers.AsEnumerable().Reverse()],
            Categories = [.. input.Categories.AsEnumerable().Reverse()],
        };
        var window = BudgetCalculator.Compute(shuffled, new DateOnly(2024, 6, 1), new DateOnly(2024, 8, 1));

        window.Months.Count.ShouldBe(3);
        foreach (var month in window.Months)
        {
            var expected = full.Month(month.Month);
            month.ReadyToAssign.ShouldBe(expected.ReadyToAssign);
            month.TotalAvailable.ShouldBe(expected.TotalAvailable);
            foreach (var cell in month.Categories)
            {
                cell.ShouldBeEquivalentTo(expected.Category(cell.CategoryId));
            }
        }
    }

    [Fact]
    public void An_empty_budget_computes_zeros_for_the_requested_range()
    {
        var b = new BudgetBuilder();
        var rent = b.Category("Rent");
        var snapshot = b.Compute("2026-01", "2026-03");
        snapshot.Months.Select(m => m.Month).ShouldBe([M("2026-01"), M("2026-02"), M("2026-03")]);
        snapshot.Months.ShouldAllBe(m => m.ReadyToAssign == 0 && m.TotalAvailable == 0);
        snapshot.Cell(rent, M("2026-02")).Available.ShouldBe(0);
        BudgetCalculator.ComputeMonth(b.Build(), D("2026-02-14")).Month.ShouldBe(M("2026-02"));
    }

    [Fact]
    public void Lookups_outside_the_range_throw()
    {
        var b = new BudgetBuilder();
        var rent = b.Category("Rent");
        var snapshot = b.Compute("2026-01", "2026-01");
        Should.Throw<KeyNotFoundException>(() => snapshot.Month(M("2026-02")));
        Should.Throw<KeyNotFoundException>(() => snapshot.Cell(rent, M("2025-12")));
        Should.Throw<KeyNotFoundException>(() => snapshot.Cell(Guid.NewGuid(), M("2026-01")));
        Should.Throw<KeyNotFoundException>(() => snapshot.Month(M("2026-01")).Category(Guid.NewGuid()));
    }

    [Fact]
    public void Invalid_input_is_rejected()
    {
        var b = new BudgetBuilder();
        var checking = b.Account("Checking", AccountType.Checking);
        var rent = b.Category("Rent");
        var input = b.Build();
        var month = M("2026-01");

        Should.Throw<ArgumentException>(() => BudgetCalculator.Compute(input, month, month.AddMonths(-1)));
        Should.Throw<ArgumentException>(() => BudgetCalculator.Compute(input with { Accounts = [.. input.Accounts, input.Accounts[0]] }, month, month));
        Should.Throw<ArgumentException>(() => BudgetCalculator.Compute(input with { Categories = [.. input.Categories, input.Categories[^1]] }, month, month));
        Should.Throw<ArgumentException>(() => BudgetCalculator.Compute(input with { Groups = [.. input.Groups, input.Groups[^1]] }, month, month));
        Should.Throw<ArgumentException>(() => BudgetCalculator.Compute(input with { Activity = [new(rent, month, Guid.NewGuid(), -1)] }, month, month));
        Should.Throw<ArgumentException>(() => BudgetCalculator.Compute(input with { Activity = [new(Guid.NewGuid(), month, checking, -1)] }, month, month));
        Should.Throw<ArgumentException>(() => BudgetCalculator.Compute(input with { Assignments = [new(Guid.NewGuid(), month, 1)] }, month, month));
        Should.Throw<ArgumentException>(() => BudgetCalculator.Compute(input with { CardTransfers = [new(Guid.NewGuid(), checking, month, 1)] }, month, month));
        Should.Throw<ArgumentException>(() => BudgetCalculator.Compute(
            input with { Categories = [.. input.Categories, new BudgetCategory(Guid.NewGuid(), Guid.NewGuid(), "Orphan", 0)] }, month, month));
        Should.Throw<ArgumentException>(() => BudgetCalculator.Compute(
            input with { Categories = [.. input.Categories, new BudgetCategory(Guid.NewGuid(), EverydayGroup, "Pay", 0, BudgetCategoryKind.CreditCardPayment, checking)] }, month, month));
    }

    [Fact]
    public void A_credit_account_may_have_only_one_payment_category()
    {
        var b = new BudgetBuilder();
        var visa = b.Account("Visa", AccountType.CreditCard);
        var input = b.Build();
        var duplicate = new BudgetCategory(Guid.NewGuid(), SystemIds.CreditCardPaymentsGroup, "Pay again", 5, BudgetCategoryKind.CreditCardPayment, visa);
        Should.Throw<ArgumentException>(() => BudgetCalculator.Compute(input with { Categories = [.. input.Categories, duplicate] }, M("2026-01"), M("2026-01")));
    }

    [Fact]
    public void Projects_entities_into_budget_input()
    {
        var visa = Account.Create("Visa", AccountType.CreditCard, D("2026-01-01"));
        var loan = Account.Create("Loan", AccountType.Loan, D("2026-01-01"));
        var accounts = new[] { BudgetAccount.From(visa), BudgetAccount.From(loan) }.ToDictionary(a => a.Id);
        accounts[visa.Id].IsBudgetCredit.ShouldBeTrue();
        accounts[loan.Id].IsOnBudget.ShouldBeFalse();
        accounts[loan.Id].IsBudgetCash.ShouldBeFalse();

        BudgetCategory.From(new Category { Id = SystemIds.ReadyToAssignCategory, GroupId = SystemIds.InflowGroup }, accounts)
            .Kind.ShouldBe(BudgetCategoryKind.Inflow);
        BudgetCategory.From(new Category { GroupId = SystemIds.CreditCardPaymentsGroup, LinkedAccountId = visa.Id }, accounts)
            .Kind.ShouldBe(BudgetCategoryKind.CreditCardPayment);
        BudgetCategory.From(new Category { GroupId = Guid.NewGuid(), LinkedAccountId = loan.Id, IsHidden = true }, accounts)
            .ShouldSatisfyAllConditions(c => c.Kind.ShouldBe(BudgetCategoryKind.Regular), c => c.IsHidden.ShouldBeTrue());
        BudgetGroup.From(new CategoryGroup { Name = "G", SortOrder = 3, IsHidden = true }).ShouldSatisfyAllConditions(
            g => g.SortOrder.ShouldBe(3), g => g.IsHidden.ShouldBeTrue());
    }
}
