using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Budget;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain;
using Keel.Domain.Budgeting;

namespace Keel.Desktop.ViewModels.Budget;

/// <summary>
/// The Flex view of a budget month (F-BUD-6, ADR 0091): total income, total Fixed assigned, the
/// Non-monthly set-aside and one Flex number with its spending progress. It only formats the
/// <see cref="FlexSummary"/> that the budget service summed from the grid's own calculator result, so
/// every number here is a sum of numbers the grid shows. Each number drills down to the grid filtered to
/// its categories (income: to the register).
/// </summary>
public sealed partial class BudgetFlexViewModel : ViewModelBase
{
    private readonly BudgetViewModel _page;

    /// <summary>Creates the view model for <paramref name="page"/>.</summary>
    public BudgetFlexViewModel(BudgetViewModel page)
    {
        _page = page;
    }

    /// <summary>The summary shown, or null before the month is loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary), nameof(MonthTitle), nameof(IncomeText), nameof(FixedText), nameof(FixedDetail), nameof(NonMonthlyText),
        nameof(NonMonthlyDetail), nameof(FlexText), nameof(FlexBreakdown), nameof(FlexSpentText), nameof(FlexLeftText), nameof(FlexProgress), nameof(IsFlexOver),
        nameof(PaceMarker), nameof(PaceText), nameof(ProgressAutomationName), nameof(HasNoFlexCategories), nameof(IncomeAutomationName),
        nameof(FixedAutomationName), nameof(NonMonthlyAutomationName), nameof(FlexAutomationName), nameof(CardPaymentsNote), nameof(HasCardPayments))]
    public partial FlexSummary? Summary { get; private set; }

    /// <summary>Days left and safe-to-spend per day.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaceMarker), nameof(PaceText), nameof(ProgressAutomationName))]
    public partial FlexPace Pace { get; private set; }

    /// <summary>Budget currency.</summary>
    public string Currency { get; private set; } = Keel.Domain.Currency.Default;

    /// <summary>Whether numbers are shown.</summary>
    public bool HasSummary => Summary is not null;

    /// <summary>"September 2026".</summary>
    public string MonthTitle => Summary is { } s ? BudgetText.Month(s.Month) : string.Empty;

    /// <summary>Income this month.</summary>
    public string IncomeText => Summary is { } s ? M(s.Income) : string.Empty;

    /// <summary>Σ Assigned of Fixed categories.</summary>
    public string FixedText => Summary is { } s ? M(s.Fixed.Assigned) : string.Empty;

    /// <summary>"Spent $1,500.00".</summary>
    public string FixedDetail => Summary is { } s ? LedgerText.Format(Strings.Flex_FixedDetail, M(s.Fixed.Spent)) : string.Empty;

    /// <summary>Σ Assigned of Non-monthly categories (this month's set-aside).</summary>
    public string NonMonthlyText => Summary is { } s ? M(s.NonMonthly.Assigned) : string.Empty;

    /// <summary>"Saved so far $600.00".</summary>
    public string NonMonthlyDetail => Summary is { } s ? LedgerText.Format(Strings.Flex_NonMonthlyDetail, M(s.NonMonthly.Available)) : string.Empty;

    /// <summary>The one Flex number: Carry + Assigned of the Flex categories.</summary>
    public string FlexText => Summary is { } s ? M(s.Flex.Budgeted) : string.Empty;

    /// <summary>"Assigned $400.00 + carried in $20.00".</summary>
    public string FlexBreakdown => Summary is { } s ? LedgerText.Format(Strings.Flex_Breakdown, M(s.Flex.Assigned), M(s.Flex.Carry)) : string.Empty;

    /// <summary>"Spent $450.00".</summary>
    public string FlexSpentText => Summary is { } s ? LedgerText.Format(Strings.Flex_Spent, M(s.Flex.Spent)) : string.Empty;

    /// <summary>"$50.00 left" or "$50.00 over".</summary>
    public string FlexLeftText => Summary is { } s
        ? LedgerText.Format(s.Flex.Available < 0 ? Strings.Flex_Over : Strings.Flex_Left, M(Math.Abs(s.Flex.Available)))
        : string.Empty;

    /// <summary>Spent as a percentage of the Flex number (0–100; full when overspent).</summary>
    public double FlexProgress => Summary is { } s ? Math.Clamp(s.Flex.SpentShare, 0, 1) * 100 : 0;

    /// <summary>More was spent than the Flex number holds.</summary>
    public bool IsFlexOver => Summary is { Flex.Available: < 0 };

    /// <summary>Where "today" falls on the progress bar (0–1 of the month).</summary>
    public double PaceMarker => Pace.MonthElapsed;

    /// <summary>"12 days left · $4.16 a day is safe to spend", or "This month has ended".</summary>
    public string PaceText => Summary is null ? string.Empty
        : Pace.DaysLeft == 0 ? Strings.Flex_MonthEnded
        : LedgerText.Format(Strings.Flex_Pace, Pace.DaysLeft, M(Pace.SafePerDay));

    /// <summary>Screen-reader text of the progress bar.</summary>
    public string ProgressAutomationName => Summary is null ? Strings.Flex_ProgressName
        : LedgerText.Format(Strings.Flex_ProgressAutomation, FlexSpentText, FlexText, FlexLeftText, PaceText);

    /// <summary>No category counts as Flex (a hint explains how to tag them).</summary>
    public bool HasNoFlexCategories => Summary is { Flex.CategoryIds.Count: 0 };

    /// <summary>Whether Credit Card Payment categories exist (they belong to no bucket).</summary>
    public bool HasCardPayments => Summary is { CardPaymentCategoryIds.Count: > 0 };

    /// <summary>Explains that card payments are left out.</summary>
    public string CardPaymentsNote => Strings.Flex_CardPaymentsNote;

    /// <summary>Screen-reader text of the income tile.</summary>
    public string IncomeAutomationName => LedgerText.Format(Strings.Flex_TileAutomation, Strings.Flex_Income, IncomeText, Strings.Flex_IncomeHint);

    /// <summary>Screen-reader text of the Fixed tile.</summary>
    public string FixedAutomationName => LedgerText.Format(Strings.Flex_TileAutomation, Strings.Flex_Fixed, FixedText, FixedDetail);

    /// <summary>Screen-reader text of the Non-monthly tile.</summary>
    public string NonMonthlyAutomationName => LedgerText.Format(Strings.Flex_TileAutomation, Strings.Flex_NonMonthly, NonMonthlyText, NonMonthlyDetail);

    /// <summary>Screen-reader text of the Flex number.</summary>
    public string FlexAutomationName => LedgerText.Format(Strings.Flex_TileAutomation, Strings.Flex_FlexNumber, FlexText, FlexBreakdown);

    /// <summary>Shows <paramref name="month"/> as seen on <paramref name="today"/>.</summary>
    public void Update(BudgetMonthDto? month, DateOnly today)
    {
        Currency = month?.ReadyToAssign.Currency ?? Keel.Domain.Currency.Default;
        Pace = month?.Flex?.Pace(today) ?? default;
        Summary = month?.Flex;
    }

    /// <summary>Drill-down: the income of the month in the register.</summary>
    [RelayCommand]
    public void ShowIncome() => _page.OpenIncome();

    /// <summary>Drill-down: the grid with the Fixed categories.</summary>
    [RelayCommand]
    public void ShowFixed() => _page.ShowFlexCategories(FlexKind.Fixed);

    /// <summary>Drill-down: the grid with the Non-monthly categories.</summary>
    [RelayCommand]
    public void ShowNonMonthly() => _page.ShowFlexCategories(FlexKind.NonMonthly);

    /// <summary>Drill-down: the grid with the Flex categories.</summary>
    [RelayCommand]
    public void ShowFlex() => _page.ShowFlexCategories(FlexKind.Flex);

    private string M(long amount) => LedgerText.Money(amount, Currency);
}
