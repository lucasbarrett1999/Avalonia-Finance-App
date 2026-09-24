using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Domain.Entities;

namespace Keel.Domain.Tests.Budgeting;

/// <summary>
/// Deterministic generator of aggregated budget input: every (month, category, account) cell
/// gets a row, like a busy ledger after <c>GROUP BY category, month, account</c>. Also linked into
/// Keel.Benchmarks.
/// </summary>
public static class BudgetInputGenerator
{
    /// <summary>Generates input starting at <paramref name="firstMonth"/>.</summary>
    /// <param name="months">Number of months with data.</param>
    /// <param name="categories">Number of regular categories (spread over groups of 10).</param>
    /// <param name="accounts">Number of accounts: about half cash, the rest credit, and one tracking when ≥ 4.</param>
    /// <param name="seed">Random seed; the same seed always yields the same input.</param>
    /// <param name="firstMonth">First month (defaults to 2024-01).</param>
    /// <param name="density">Probability that a (month, category, account) cell has activity.</param>
    public static BudgetInput Generate(int months, int categories, int accounts, int seed, DateOnly? firstMonth = null, double density = 1.0)
    {
        var random = new Random(seed);
        var start = firstMonth ?? new DateOnly(2024, 1, 1);
        var ids = 0;
        Guid Next() => new($"10000000-0000-7000-8000-{++ids:x12}");

        var accountList = new List<BudgetAccount>();
        for (var i = 0; i < accounts; i++)
        {
            var type = accounts >= 4 && i == accounts - 1 ? AccountType.Investment
                : i % 2 == 0 ? (i % 4 == 0 ? AccountType.Checking : AccountType.Savings)
                : (i % 3 == 0 ? AccountType.LineOfCredit : AccountType.CreditCard);
            accountList.Add(new BudgetAccount(Next(), $"Account {i}", type, AccountTypeInfo.IsOnBudgetByDefault(type), IsClosed: i == 2));
        }

        var groups = new List<BudgetGroup>
        {
            new(SystemIds.InflowGroup, "Inflow", 0, IsSystem: true),
            new(SystemIds.CreditCardPaymentsGroup, "Credit Card Payments", 1, IsSystem: true),
        };
        var categoryList = new List<BudgetCategory>
        {
            new(SystemIds.ReadyToAssignCategory, SystemIds.InflowGroup, "Ready to Assign", 0, BudgetCategoryKind.Inflow),
        };

        foreach (var card in accountList.Where(a => a.IsBudgetCredit))
        {
            categoryList.Add(new BudgetCategory(Next(), SystemIds.CreditCardPaymentsGroup, "Pay_" + card.Name, categoryList.Count, BudgetCategoryKind.CreditCardPayment, card.Id));
        }

        var regular = new List<Guid>();
        for (var i = 0; i < categories; i++)
        {
            if (i % 10 == 0)
            {
                groups.Add(new BudgetGroup(Next(), $"Group {i / 10}", groups.Count, IsHidden: i / 10 == 3));
            }

            var id = Next();
            regular.Add(id);
            categoryList.Add(new BudgetCategory(id, groups[^1].Id, $"Category {i}", i % 10, IsHidden: i % 17 == 5));
        }

        var cash = accountList.Where(a => a.IsBudgetCash).ToList();
        var activity = new List<ActivityTotal>(months * (categories + 1) * accounts);
        var assignments = new List<AssignmentTotal>(months * categories);
        var transfers = new List<CardTransferTotal>();
        for (var m = 0; m < months; m++)
        {
            var month = start.AddMonths(m);
            foreach (var account in cash)
            {
                activity.Add(new ActivityTotal(SystemIds.ReadyToAssignCategory, month, account.Id, random.NextInt64(1_000_00, 6_000_00)));
            }

            foreach (var category in regular)
            {
                assignments.Add(new AssignmentTotal(category, month, random.Next(10) == 0 ? 0 : random.NextInt64(0, 300_00)));
                foreach (var account in accountList)
                {
                    if (random.NextDouble() >= density)
                    {
                        continue;
                    }

                    // Mostly spending, sometimes a net refund.
                    var amount = random.Next(12) == 0 ? random.NextInt64(1, 50_00) : -random.NextInt64(1, 60_00);
                    activity.Add(new ActivityTotal(category, month, account.Id, amount));
                }
            }

            foreach (var card in accountList.Where(a => a.IsBudgetCredit))
            {
                transfers.Add(new CardTransferTotal(card.Id, cash[random.Next(cash.Count)].Id, month, random.NextInt64(100_00, 2_500_00)));
            }

            activity.Add(new ActivityTotal(null, month, cash[0].Id, -random.NextInt64(0, 20_00)));
        }

        return new BudgetInput(accountList, groups, categoryList, activity, assignments, transfers);
    }
}
