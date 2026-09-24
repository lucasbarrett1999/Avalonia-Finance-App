using Keel.Application.Accounts;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Domain;

namespace Keel.Desktop.Tests;

/// <summary>
/// A small ledger for the review and rules screens: approved history the learner can use (4 × TRADER
/// JOES in Groceries, 3 × Taco Truck in Dining) and four unapproved imports, oldest first.
/// </summary>
internal sealed class ReviewTestLedger
{
    private static CancellationToken Ct => CancellationToken.None;

    public required Guid Checking { get; init; }

    public required Guid Savings { get; init; }

    public required Guid Groceries { get; init; }

    public required Guid Dining { get; init; }

    public required Guid Household { get; init; }

    /// <summary>Unapproved: TRADER JOES, Taco Truck, Corner Store, Costco (oldest first).</summary>
    public required IReadOnlyList<Guid> Pending { get; init; }

    public static Task<ReviewTestLedger> CreateAsync(TestHost host, bool withPending = true) => Task.Run(async () =>
    {
        var accounts = host.Get<IAccountService>();
        var categories = host.Get<ICategoryService>();
        var transactions = host.Get<ITransactionService>();
        var checking = await accounts.CreateAccountAsync(new CreateAccountRequest("Checking", AccountType.Checking, "USD", new DateOnly(2026, 7, 1), 500_000), Ct);
        var savings = await accounts.CreateAccountAsync(new CreateAccountRequest("Savings", AccountType.Savings, "USD", new DateOnly(2026, 7, 1), 0), Ct);
        var groceries = (await categories.CreateCategoryAsync("Everyday", "Groceries", Ct)).Id;
        var dining = (await categories.CreateCategoryAsync("Everyday", "Dining", Ct)).Id;
        var household = (await categories.CreateCategoryAsync("Everyday", "Household", Ct)).Id;
        for (var i = 0; i < 4; i++)
        {
            await transactions.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 7, 3 + i), -4_000 - (i * 100), "TRADER JOES", groceries, null), Ct);
        }

        for (var i = 0; i < 3; i++)
        {
            await transactions.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 7, 10 + i), -1_250, "Taco Truck", dining, null), Ct);
        }

        var pending = new List<Guid>();
        if (withPending)
        {
            var rows = new (string Payee, long Amount)[] { ("TRADER JOES", -4_320), ("Taco Truck", -1_300), ("Corner Store", -899), ("Costco", -15_640) };
            for (var i = 0; i < rows.Length; i++)
            {
                var saved = await transactions.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 1 + i), rows[i].Amount, rows[i].Payee, null, null, IsApproved: false), Ct);
                pending.Add(saved.Id);
            }
        }

        return new ReviewTestLedger
        {
            Checking = checking.Id,
            Savings = savings.Id,
            Groceries = groceries,
            Dining = dining,
            Household = household,
            Pending = pending,
        };
    });
}
