using System.Globalization;
using Keel.Application.Alerts;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Bills;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Alerts;

/// <summary>One notification-center entry (F-REC-3): kind icon, a one-line title and the numbers behind it.</summary>
public sealed class AlertItemViewModel
{
    /// <summary>Creates the entry.</summary>
    public AlertItemViewModel(AlertDto alert, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(alert);
        Alert = alert;
        var payee = string.IsNullOrWhiteSpace(alert.PayeeName) ? Strings.Alerts_UnknownPayee : alert.PayeeName;
        var amount = alert.Amount is { } a ? LedgerText.Money(Math.Abs(a.Amount), a.Currency) : string.Empty;
        var previous = alert.PreviousAmount is { } p ? LedgerText.Money(Math.Abs(p.Amount), p.Currency) : string.Empty;
        var date = alert.ExpectedDate is { } d ? BillsFormat.ShortDate(d) : string.Empty;
        (Title, Detail, IconKey) = alert.Kind switch
        {
            AlertKind.PriceIncrease => (
                LedgerText.Format(Strings.Alerts_PriceIncreaseTitle, payee, (alert.IncreasePercent ?? 0m).ToString("0.#", CultureInfo.CurrentCulture)),
                LedgerText.Format(Strings.Alerts_PriceIncreaseDetail, previous, amount),
                "Icon.Report.Trend"),
            AlertKind.MissingExpected => (
                LedgerText.Format(Strings.Alerts_MissingTitle, payee, alert.DaysLate ?? 0),
                LedgerText.Format(Strings.Alerts_MissingDetail, date, amount),
                "Icon.Report.Alert"),
            AlertKind.NewRecurring => (
                LedgerText.Format(Strings.Alerts_NewTitle, payee),
                LedgerText.Format(Strings.Alerts_NewDetail, amount, date),
                "Icon.Bills"),
            _ => (
                LedgerText.Format(Strings.Alerts_TrialTitle, payee),
                LedgerText.Format(Strings.Alerts_TrialDetail, amount, previous),
                "Icon.CreditCard"),
        };
        var age = nowUtc - alert.CreatedAt;
        TimeText = age.TotalMinutes < 1 ? Strings.Alerts_JustNow
            : age.TotalHours < 1 ? LedgerText.Format(Strings.Alerts_MinutesAgo, (int)age.TotalMinutes)
            : age.TotalDays < 1 ? LedgerText.Format(Strings.Alerts_HoursAgo, (int)age.TotalHours)
            : BillsFormat.ShortDate(DateOnly.FromDateTime(alert.CreatedAt.ToLocalTime()));
    }

    /// <summary>The alert.</summary>
    public AlertDto Alert { get; }

    /// <summary>Id.</summary>
    public Guid Id => Alert.Id;

    /// <summary>Kind.</summary>
    public AlertKind Kind => Alert.Kind;

    /// <summary>"Netflix went up 12.5%".</summary>
    public string Title { get; }

    /// <summary>"$15.49 → $17.49".</summary>
    public string Detail { get; }

    /// <summary>Icon resource key for the kind.</summary>
    public string IconKey { get; }

    /// <summary>"3 h ago".</summary>
    public string TimeText { get; }

    /// <summary>Not read yet (bold, with a dot).</summary>
    public bool IsUnread => Alert.ReadAt is null && Alert.DismissedAt is null;

    /// <summary>A warning (price increase, missing, trial) rather than information.</summary>
    public bool IsWarning => Alert.Kind != AlertKind.NewRecurring;

    /// <summary>Screen-reader text.</summary>
    public string AutomationName => Title + ". " + Detail + ". " + TimeText;
}
