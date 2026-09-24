namespace Keel.Domain;

/// <summary>
/// Account classification rules from PRD 6.3.
/// </summary>
/// <remarks>
/// | Type                                   | On budget by default | Cash-like | Liability |
/// |----------------------------------------|----------------------|-----------|-----------|
/// | Checking, Savings, Cash                | Yes                  | Yes       | No        |
/// | Credit Card, Line of Credit            | Yes                  | No        | Yes       |
/// | Loan, Investment, Other Asset/Liability| No (tracking)        | No        | Loan and Other Liability |
/// </remarks>
public static class AccountTypeInfo
{
    /// <summary>All account types in display order.</summary>
    public static IReadOnlyList<AccountType> All { get; } = Enum.GetValues<AccountType>();

    /// <summary>Whether a new account of this type is on budget unless the user overrides it.</summary>
    public static bool IsOnBudgetByDefault(AccountType type) => type switch
    {
        AccountType.Checking or AccountType.Savings or AccountType.Cash => true,
        AccountType.CreditCard or AccountType.LineOfCredit => true,
        AccountType.Loan or AccountType.Investment or AccountType.OtherAsset or AccountType.OtherLiability => false,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Whether the account holds money that can be assigned (when on budget).</summary>
    public static bool IsCashLike(AccountType type) => type switch
    {
        AccountType.Checking or AccountType.Savings or AccountType.Cash => true,
        AccountType.CreditCard or AccountType.LineOfCredit or AccountType.Loan or AccountType.Investment
            or AccountType.OtherAsset or AccountType.OtherLiability => false,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Whether the account's balance is money owed.</summary>
    public static bool IsLiability(AccountType type) => type switch
    {
        AccountType.CreditCard or AccountType.LineOfCredit or AccountType.Loan or AccountType.OtherLiability => true,
        AccountType.Checking or AccountType.Savings or AccountType.Cash or AccountType.Investment
            or AccountType.OtherAsset => false,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// Whether the account participates in the budget through a Credit Card Payment category
    /// (on-budget credit accounts, 6.4.5).
    /// </summary>
    public static bool IsCredit(AccountType type) => type is AccountType.CreditCard or AccountType.LineOfCredit;

    /// <summary>
    /// Whether the user may override the on-budget default. PRD 6.2: Savings and Cash only.
    /// </summary>
    public static bool CanOverrideOnBudget(AccountType type) => type is AccountType.Savings or AccountType.Cash;

    /// <summary>Returns true when the requested on-budget flag is allowed for the type.</summary>
    public static bool IsOnBudgetAllowed(AccountType type, bool isOnBudget) =>
        isOnBudget == IsOnBudgetByDefault(type) || CanOverrideOnBudget(type);

    /// <summary>Sidebar group for an account with the given type and on-budget flag.</summary>
    public static AccountGroup GroupOf(AccountType type, bool isOnBudget) =>
        !isOnBudget ? AccountGroup.Tracking
        : IsCredit(type) ? AccountGroup.Credit
        : AccountGroup.Cash;
}
