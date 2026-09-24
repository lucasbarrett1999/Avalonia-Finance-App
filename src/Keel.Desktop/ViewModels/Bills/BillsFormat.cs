using System.Globalization;
using Keel.Application.Recurring;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Bills;

/// <summary>Text helpers for the Bills screen, scheduled transactions and alerts (all text from Strings.resx).</summary>
public static class BillsFormat
{
    /// <summary>"Monthly", "Every 2 weeks", …</summary>
    public static string Cadence(RecurrenceCadence cadence) => Lookup("Cadence_" + cadence) ?? cadence.ToString();

    /// <summary>"Active", "Detected", …</summary>
    public static string Status(RecurringStatus status) => Lookup("RecurringStatus_" + status) ?? status.ToString();

    /// <summary>Money text.</summary>
    public static string Money(Money money) => LedgerText.Money(money.Amount, money.Currency);

    /// <summary>"Sep 24".</summary>
    public static string ShortDate(DateOnly date) => date.ToString("MMM d", CultureInfo.CurrentCulture);

    /// <summary>"Sep 24, 2026".</summary>
    public static string LongDate(DateOnly date) => date.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    /// <summary>"Wed, Sep 24".</summary>
    public static string DayDate(DateOnly date) => date.ToString("ddd, MMM d", CultureInfo.CurrentCulture);

    /// <summary>Relative due text: "Today", "Tomorrow", "In 5 days", "3 days ago".</summary>
    public static string Due(DateOnly date, DateOnly today) => (date.DayNumber - today.DayNumber) switch
    {
        0 => Strings.Bills_DueToday,
        1 => Strings.Bills_DueTomorrow,
        -1 => Strings.Bills_DueYesterday,
        > 1 and var d => LedgerText.Format(Strings.Bills_DueInDays, d),
        var d => LedgerText.Format(Strings.Bills_DueDaysAgo, -d),
    };

    /// <summary>The designed summary of a detection run for the status strip.</summary>
    public static string Summary(RecurringDetectionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.Created + summary.Updated + summary.Ended == 0
            ? Strings.Bills_DetectionNoChanges
            : LedgerText.Format(Strings.Bills_DetectionSummary, summary.Created, summary.Updated, summary.Ended);
    }

    private static string? Lookup(string key) => Strings.ResourceManager.GetString(key, Strings.Culture);
}
