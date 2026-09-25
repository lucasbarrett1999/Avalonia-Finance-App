namespace Keel.Infrastructure.Import.Monarch;

/// <summary>
/// Monarch's default categories and their groups. A Monarch export names only the category, so a
/// category missing from the budget file is created in the group Monarch uses for it (unknown, custom
/// categories go to "Other"). Income categories map to Ready to Assign; transfer-like ones get no category.
/// </summary>
public static class MonarchCategories
{
    /// <summary>The group for categories this table does not know.</summary>
    public const string OtherGroup = "Other";

    /// <summary>Default categories by group, as Monarch names them.</summary>
    public static IReadOnlyDictionary<string, string[]> Groups { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Auto & Transport"] = ["Auto Payment", "Public Transit", "Gas", "Auto Maintenance", "Parking & Tolls", "Taxi & Ride Shares"],
        ["Housing"] = ["Mortgage", "Rent", "Home Improvement"],
        ["Bills & Utilities"] = ["Garbage", "Water", "Gas & Electric", "Internet & Cable", "Phone"],
        ["Food & Dining"] = ["Groceries", "Restaurants & Bars", "Coffee Shops"],
        ["Travel & Lifestyle"] = ["Travel & Vacation", "Entertainment & Recreation", "Personal", "Pets", "Fun Money"],
        ["Shopping"] = ["Shopping", "Clothing", "Furniture & Housewares", "Electronics"],
        ["Children"] = ["Child Care", "Child Activities"],
        ["Education"] = ["Student Loans", "Education"],
        ["Health & Wellness"] = ["Medical", "Dentist", "Fitness"],
        ["Financial"] = ["Loan Repayment", "Financial & Legal Services", "Financial Fees", "Cash & ATM", "Insurance", "Taxes"],
        ["Gifts & Donations"] = ["Gifts", "Charity"],
        ["Business"] =
        [
            "Advertising & Promotion", "Business Utilities & Communication", "Employee Wages & Contract Labor", "Business Travel & Meals",
            "Business Auto Expenses", "Business Insurance", "Office Supplies & Expenses", "Office Rent", "Postage & Shipping",
        ],
        [OtherGroup] = ["Check", "Miscellaneous"],
    };

    /// <summary>Income categories: their rows are Inflow: Ready to Assign in Keel.</summary>
    public static IReadOnlySet<string> Income { get; } = new HashSet<string>(["Paychecks", "Interest", "Business Income", "Other Income"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Money moving between the user's own accounts: no category (transfer detection pairs them).</summary>
    public static IReadOnlySet<string> Transfers { get; } = new HashSet<string>(["Transfer", "Credit Card Payment", "Balance Adjustments"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Monarch's "no category yet".</summary>
    public const string Uncategorized = "Uncategorized";

    private static readonly Dictionary<string, string> GroupByCategory = Groups
        .SelectMany(g => g.Value.Select(c => (Category: c, Group: g.Key)))
        .ToDictionary(x => x.Category, x => x.Group, StringComparer.OrdinalIgnoreCase);

    /// <summary>The group of <paramref name="category"/>: Monarch's default group, else <see cref="OtherGroup"/>.</summary>
    public static string GroupOf(string category) => GroupByCategory.TryGetValue(category.Trim(), out var group) ? group : OtherGroup;
}
