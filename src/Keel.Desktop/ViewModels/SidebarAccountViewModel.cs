using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Accounts;
using Keel.Desktop.Services;
using Keel.Domain;

namespace Keel.Desktop.ViewModels;

/// <summary>A sidebar group of accounts (Cash, Credit, Tracking, Closed) with its total.</summary>
public sealed partial class SidebarAccountGroupViewModel : ObservableObject
{
    /// <summary>Creates the group.</summary>
    public SidebarAccountGroupViewModel(AccountGroup? group, string title)
    {
        Group = group;
        Title = title;
    }

    /// <summary>The account group, or null for closed accounts.</summary>
    public AccountGroup? Group { get; }

    /// <summary>Heading.</summary>
    public string Title { get; }

    /// <summary>Accounts in sidebar order.</summary>
    public ObservableCollection<SidebarAccountViewModel> Accounts { get; } = [];

    /// <summary>Total balance text.</summary>
    [ObservableProperty]
    public partial string TotalText { get; set; } = string.Empty;
}

/// <summary>An account row in the sidebar (PRD 9.1): name, balance, and account actions.</summary>
public sealed partial class SidebarAccountViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    /// <summary>Creates the row.</summary>
    public SidebarAccountViewModel(ShellViewModel shell, AccountDto account)
    {
        _shell = shell;
        Account = account;
    }

    /// <summary>The account.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Id), nameof(Name), nameof(BalanceText), nameof(IsNegative), nameof(IconKey), nameof(AutomationName), nameof(IsClosed))]
    public partial AccountDto Account { get; set; }

    /// <summary>Account id.</summary>
    public Guid Id => Account.Id;

    /// <summary>Name.</summary>
    public string Name => Account.Name;

    /// <summary>Whether the account is closed.</summary>
    public bool IsClosed => Account.IsClosed;

    /// <summary>Balance as currency.</summary>
    public string BalanceText => LedgerText.Money(Account.Balance.Amount, Account.Balance.Currency);

    /// <summary>Negative balances are shown in the negative colour with a minus sign.</summary>
    public bool IsNegative => Account.Balance.IsNegative;

    /// <summary>Icon by group.</summary>
    public string IconKey => Account.Group switch
    {
        AccountGroup.Credit => "Icon.CreditCard",
        AccountGroup.Tracking => "Icon.Tracking",
        _ => "Icon.Wallet",
    };

    /// <summary>Screen-reader text: name and balance.</summary>
    public string AutomationName => Name + ", " + BalanceText;

    /// <summary>Whether this account's register is shown.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Navigate() => _shell.OpenAccount(Id);

    [RelayCommand]
    private Task EditAsync() => _shell.EditAccountAsync(Account);

    [RelayCommand]
    private Task MoveUpAsync() => _shell.MoveAccountAsync(this, -1);

    [RelayCommand]
    private Task MoveDownAsync() => _shell.MoveAccountAsync(this, 1);
}
