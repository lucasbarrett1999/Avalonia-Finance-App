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
