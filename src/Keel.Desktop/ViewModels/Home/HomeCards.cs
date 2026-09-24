using CommunityToolkit.Mvvm.Input;
using Keel.Desktop.ViewModels.Reports;

namespace Keel.Desktop.ViewModels.Home;

/// <summary>An account row on the Accounts card.</summary>
/// <param name="Id">Account.</param>
/// <param name="Name">Name.</param>
/// <param name="Balance">Ledger balance.</param>
/// <param name="Currency">Currency.</param>
/// <param name="Open">Opens the register.</param>
public sealed record HomeAccountRow(Guid Id, string Name, long Balance, string Currency, IRelayCommand<Guid> Open)
{
    /// <summary>Balance text.</summary>
    public string BalanceText => ReportFormat.Money(Balance, Currency);

    /// <summary>Negative balance (money owed or overdrawn).</summary>
    public bool IsNegative => Balance < 0;
}

/// <summary>An account group (Cash, Credit, Tracking) on the Accounts card.</summary>
/// <param name="Title">Group name.</param>
/// <param name="Total">Σ balances.</param>
/// <param name="Currency">Currency.</param>
/// <param name="Accounts">Accounts in sidebar order.</param>
public sealed record HomeAccountGroup(string Title, long Total, string Currency, IReadOnlyList<HomeAccountRow> Accounts)
{
    /// <summary>Total text.</summary>
    public string TotalText => ReportFormat.Money(Total, Currency);

    /// <summary>Negative total.</summary>
    public bool IsNegative => Total < 0;
}

/// <summary>A budget alert: an overspent or underfunded category this month.</summary>
/// <param name="CategoryId">Category.</param>
/// <param name="Name">Category name.</param>
/// <param name="IsOverspent">Overspent (otherwise underfunded).</param>
/// <param name="KindText">"Overspent" or "Underfunded" (words, not colour alone).</param>
/// <param name="Amount">Overspent or underfunded amount (positive).</param>
/// <param name="Currency">Currency.</param>
public sealed record HomeBudgetAlert(Guid CategoryId, string Name, bool IsOverspent, string KindText, long Amount, string Currency)
{
    /// <summary>Amount text.</summary>
    public string AmountText => ReportFormat.Money(Amount, Currency);
}

/// <summary>A bill due soon on the Upcoming bills card.</summary>
/// <param name="Date">Due date.</param>
/// <param name="Payee">Payee.</param>
/// <param name="Amount">Amount due (positive).</param>
/// <param name="Currency">Currency.</param>
/// <param name="ItemId">Recurring item, when the bill is one.</param>
/// <param name="AccountId">Account.</param>
/// <param name="Today">Today.</param>
/// <param name="IsUnconfirmed">A detected item not yet confirmed.</param>
/// <param name="Open">Opens the bill.</param>
public sealed record HomeBillRow(DateOnly Date, string Payee, long Amount, string Currency, Guid? ItemId, Guid? AccountId, DateOnly Today, bool IsUnconfirmed, IRelayCommand<HomeBillRow> Open)
{
    /// <summary>"Today", "Tomorrow", "In 3 days" or "2 days ago".</summary>
    public string DueText => Bills.BillsFormat.Due(Date, Today);

    /// <summary>Due before today.</summary>
    public bool IsOverdue => Date < Today;

    /// <summary>Amount text.</summary>
    public string AmountText => ReportFormat.Money(Amount, Currency);

    /// <summary>Screen-reader text.</summary>
    public string AutomationName => Payee + ", " + AmountText + ", " + DueText;
}
