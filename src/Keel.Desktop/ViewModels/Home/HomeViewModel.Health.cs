using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Reports;
using Keel.Domain.Budgeting;

namespace Keel.Desktop.ViewModels;

/// <summary>The age-of-money card of the dashboard (F-REP-5): today's age with a 12-month sparkline, linking to Budget health.</summary>
public sealed partial class HomeViewModel
{
    /// <summary>Age of money now, e.g. "43 days" (or "—").</summary>
    [ObservableProperty]
    public partial string AgeOfMoneyText { get; private set; } = string.Empty;

    /// <summary>What the number means, or the empty-state line.</summary>
    [ObservableProperty]
    public partial string AgeOfMoneyDetail { get; private set; } = string.Empty;

    /// <summary>Age of money at the last 12 month ends (sparkline).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAgeOfMoneyTrend))]
    public partial IReadOnlyList<double> AgeOfMoneyValues { get; private set; } = [];

    /// <summary>Whether the sparkline has at least two points.</summary>
    public bool HasAgeOfMoneyTrend => AgeOfMoneyValues.Count > 1;

    /// <summary>Opens the Budget health report.</summary>
    [RelayCommand]
    public void OpenBudgetHealth() => _navigation.NavigateTo<ReportsViewModel>(ReportKind.BudgetHealth);

    private async Task LoadHealthCardAsync(DateOnly today, int version)
    {
        var report = await _reports.GetAgeOfMoneyAsync(BudgetMonth.Of(today).AddMonths(-11), today, CancellationToken.None);
        if (version != _version)
        {
            return;
        }

        var latest = report.Latest;
        AgeOfMoneyText = BudgetHealthReportViewModel.DaysText(latest?.Days);
        AgeOfMoneyDetail = latest?.Days is { } days
            ? LedgerText.Format(Strings.Health_AgeOfMoneyDetail, latest.OutflowCount, days)
            : Strings.Health_Home_Empty;
        AgeOfMoneyValues = report.Points.Where(p => p.Days is not null).Select(p => (double)p.Days!.Value).ToList();
    }
}
