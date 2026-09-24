using Keel.Domain;
using Keel.Domain.Entities;

namespace Keel.Domain.Tests;

public class AccountTypeInfoTests
{
    // The PRD 6.3 classification table, row by row.
    [Theory]
    [InlineData(AccountType.Checking, true, true, false)]
    [InlineData(AccountType.Savings, true, true, false)]
    [InlineData(AccountType.Cash, true, true, false)]
    [InlineData(AccountType.CreditCard, true, false, true)]
    [InlineData(AccountType.LineOfCredit, true, false, true)]
    [InlineData(AccountType.Loan, false, false, true)]
    [InlineData(AccountType.Investment, false, false, false)]
    [InlineData(AccountType.OtherAsset, false, false, false)]
    [InlineData(AccountType.OtherLiability, false, false, true)]
    public void Classifies_account_types_per_PRD_6_3(AccountType type, bool onBudget, bool cashLike, bool liability)
    {
        AccountTypeInfo.IsOnBudgetByDefault(type).ShouldBe(onBudget);
        AccountTypeInfo.IsCashLike(type).ShouldBe(cashLike);
        AccountTypeInfo.IsLiability(type).ShouldBe(liability);
    }

    [Fact]
    public void Covers_every_account_type()
    {
        AccountTypeInfo.All.Count.ShouldBe(9);
        foreach (var type in AccountTypeInfo.All)
        {
            Should.NotThrow(() => AccountTypeInfo.IsOnBudgetByDefault(type));
            Should.NotThrow(() => AccountTypeInfo.IsCashLike(type));
            Should.NotThrow(() => AccountTypeInfo.IsLiability(type));
        }
    }

    [Fact]
    public void Unknown_values_throw()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => AccountTypeInfo.IsCashLike((AccountType)99));
    }

    [Theory]
    [InlineData(AccountType.Savings, true)]
    [InlineData(AccountType.Cash, true)]
    [InlineData(AccountType.Checking, false)]
    [InlineData(AccountType.CreditCard, false)]
    [InlineData(AccountType.Investment, false)]
    public void Only_savings_and_cash_may_override_on_budget(AccountType type, bool canOverride)
    {
        AccountTypeInfo.CanOverrideOnBudget(type).ShouldBe(canOverride);
        AccountTypeInfo.IsOnBudgetAllowed(type, !AccountTypeInfo.IsOnBudgetByDefault(type)).ShouldBe(canOverride);
        AccountTypeInfo.IsOnBudgetAllowed(type, AccountTypeInfo.IsOnBudgetByDefault(type)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(AccountType.Checking, true, AccountGroup.Cash)]
    [InlineData(AccountType.Savings, false, AccountGroup.Tracking)]
    [InlineData(AccountType.CreditCard, true, AccountGroup.Credit)]
    [InlineData(AccountType.LineOfCredit, true, AccountGroup.Credit)]
    [InlineData(AccountType.Loan, false, AccountGroup.Tracking)]
    public void Groups_accounts_for_the_sidebar(AccountType type, bool onBudget, AccountGroup expected)
    {
        AccountTypeInfo.GroupOf(type, onBudget).ShouldBe(expected);
    }

    [Fact]
    public void New_accounts_take_the_type_default()
    {
        var card = Account.Create("Visa", AccountType.CreditCard, new DateOnly(2026, 8, 1));
        card.IsOnBudget.ShouldBeTrue();
        card.Group.ShouldBe(AccountGroup.Credit);

        var house = Account.Create("House", AccountType.OtherAsset, new DateOnly(2026, 8, 1), "eur");
        house.IsOnBudget.ShouldBeFalse();
        house.Currency.ShouldBe("EUR");
    }
}
