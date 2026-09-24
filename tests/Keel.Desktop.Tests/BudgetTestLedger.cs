using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Budget;
using Keel.Desktop.Views;
using Keel.Domain;
using Keel.Domain.Entities;

namespace Keel.Desktop.Tests;

/// <summary>The PRD 6.4.7 worked example written through the real services of a <see cref="TestHost"/>.</summary>
internal sealed record BudgetTestLedger(Guid Checking, Guid Visa, Guid Groceries, Guid Rent, Guid PayVisa, Guid Dining)
{
    public static readonly DateOnly August = new(2026, 8, 1);

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>
    /// Checking +3,000 Ready to Assign; Rent 1,500 and Groceries 400 assigned; Rent −1,500; Visa −250
    /// and −200 Groceries; Checking → Visa 100. Plus an empty Dining category in another group.
    /// </summary>
    public static async Task<BudgetTestLedger> CreateAsync(TestHost host) => await Task.Run(async () =>
    {
        var accounts = host.Get<IAccountService>();
        var transactions = host.Get<ITransactionService>();
        var categories = host.Get<ICategoryService>();
        var budget = host.Get<IBudgetService>();
        var checking = await accounts.CreateAccountAsync(new CreateAccountRequest("Checking", AccountType.Checking, "USD", August, 0), Ct);
        var visa = await accounts.CreateAccountAsync(new CreateAccountRequest("Visa", AccountType.CreditCard, "USD", August, 0), Ct);
        var rent = await categories.CreateCategoryAsync("Bills", "Rent", Ct);
        var groceries = await categories.CreateCategoryAsync("Everyday", "Groceries", Ct);
        var dining = await categories.CreateCategoryAsync("Everyday", "Dining", Ct);
        await Save(checking.Id, "2026-08-01", 3_000_00, "Employer", SystemIds.ReadyToAssignCategory);
        await budget.AssignAsync(rent.Id, August, 1_500_00, Ct);
        await budget.AssignAsync(groceries.Id, August, 400_00, Ct);
        await Save(checking.Id, "2026-08-05", -1_500_00, "Landlord", rent.Id);
        await Save(visa.Id, "2026-08-10", -250_00, "Grocer", groceries.Id);
        await Save(visa.Id, "2026-08-20", -200_00, "Grocer", groceries.Id);
        await transactions.SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 25), -100_00, null, null, null, TransferAccountId: visa.Id), Ct);
        var pay = (await categories.GetCategoriesAsync(true, Ct)).Single(c => c.LinkedAccountId == visa.Id);
        return new BudgetTestLedger(checking.Id, visa.Id, groceries.Id, rent.Id, pay.Id, dining.Id);

        Task Save(Guid account, string date, long amount, string payee, Guid category) =>
            transactions.SaveAsync(new SaveTransactionRequest(null, account, DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), amount, payee, category, null), Ct);
    });
}

internal static class BudgetTestExtensions
{
    public static BudgetView Budget(this Avalonia.Controls.Window window) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<BudgetView>().Single();

    public static BudgetCategoryRowViewModel Row(this BudgetViewModel vm, Guid id) =>
        vm.FindCategory(id) ?? throw new InvalidOperationException("No row for " + id);

    /// <summary>Lets loads, messages, writes and layout settle.</summary>
    public static async Task SettleAsync(this BudgetViewModel vm)
    {
        for (var i = 0; i < 6; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await vm.WhenIdleAsync();
            await Task.Delay(15);
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
