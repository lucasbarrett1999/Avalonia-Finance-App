using Avalonia;
using Avalonia.Controls;

namespace Keel.Desktop.Views;

/// <summary>
/// View for <see cref="ViewModels.HomeViewModel"/>. Lays the cards out in two columns in PRD 9.2
/// order (row by row) and in one column when the content area is narrow.
/// </summary>
public partial class HomeView : UserControl
{
    /// <summary>Width below which the cards stack in one column.</summary>
    public const double TwoColumnMinWidth = 760;

    private bool? _twoColumns;

    /// <summary>Creates the view.</summary>
    public HomeView()
    {
        InitializeComponent();
        DashboardScroll.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
    }

    /// <summary>Whether the cards currently use two columns.</summary>
    public bool IsTwoColumn => _twoColumns ?? true;

    private void Arrange(double width)
    {
        var two = width >= TwoColumnMinWidth;
        if (_twoColumns == two)
        {
            return;
        }

        _twoColumns = two;
        Control[] cards = [ReadyToAssignCard, ReviewCard, AccountsCard, UpcomingBillsCard, BudgetAlertsCard, ForecastCard, NetWorthCard, AgeOfMoneyCard];
        for (var i = 0; i < cards.Length; i++)
        {
            Grid.SetRow(cards[i], two ? i / 2 : i);
            Grid.SetColumn(cards[i], two ? (i % 2) * 2 : 0);
            Grid.SetColumnSpan(cards[i], two ? 1 : 3);
        }
    }
}
