using System.Globalization;
using Keel.Application.Budget;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain;
using Keel.Domain.Budgeting;

namespace Keel.Desktop.ViewModels.Budget;

/// <summary>Localized text for budget numbers, targets and explanations (all strings come from Strings.resx).</summary>
public static class BudgetText
{
    /// <summary>"September 2026".</summary>
    public static string Month(DateOnly month) => month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>"Sep".</summary>
    public static string ShortMonth(DateOnly month) => month.ToString("MMM", CultureInfo.CurrentCulture);

    /// <summary>Formats money.</summary>
    public static string Money(Money money) => LedgerText.Money(money.Amount, money.Currency);

    /// <summary>Display name of a target type.</summary>
    public static string TargetType(TargetType type) => Lookup("TargetType_" + type) ?? type.ToString();

    /// <summary>One-line description of a target, e.g. "Set aside $100.00 each month".</summary>
    public static string TargetSummary(TargetProgressDto target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var amount = Money(target.Amount);
        return target.Type switch
        {
            Domain.TargetType.MonthlySetAside => LedgerText.Format(Strings.TargetSummary_MonthlySetAside, amount),
            Domain.TargetType.MonthlySpending => LedgerText.Format(Strings.TargetSummary_MonthlySpending, amount),
            Domain.TargetType.SavingsBalanceByDate => LedgerText.Format(Strings.TargetSummary_SavingsBalanceByDate, amount,
                target.TargetDate?.ToString("d", CultureInfo.CurrentCulture) ?? string.Empty, Money(target.MonthlyNeed)),
            Domain.TargetType.DebtPayment => LedgerText.Format(Strings.TargetSummary_DebtPayment, amount),
            _ => amount,
        };
    }

    /// <summary>The card line of a Credit Card Payment row (6.4.5): balance and what is not yet covered.</summary>
    public static string CardDetail(CardPaymentDto card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var balance = Money(card.CardBalance);
        return card.Difference.Amount switch
        {
            < 0 => LedgerText.Format(Strings.Budget_CardUncovered, balance, Money(card.Difference.Abs())),
            > 0 when card.CardBalance.Amount < 0 => LedgerText.Format(Strings.Budget_CardSurplus, balance, Money(card.Difference)),
            _ => LedgerText.Format(Strings.Budget_CardCovered, balance),
        };
    }

    /// <summary>Label of an explanation line.</summary>
    public static string ExplanationLabel(ExplanationLineDto line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var template = Lookup("Explain_" + line.Kind) ?? line.Kind.ToString();
        var subject = line.Kind switch
        {
            ExplanationLineKind.CoveredFromCategory => line.CategoryName,
            ExplanationLineKind.PreviousAvailable or ExplanationLineKind.CashOverspentInMonth => line.Month is { } m ? Month(m) : null,
            _ => line.AccountName,
        };
        return LedgerText.Format(template, subject ?? string.Empty);
    }

    private static string? Lookup(string key) => Strings.ResourceManager.GetString(key, Strings.Culture);
}
