using System.Globalization;
using Keel.Application.Recurring;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Alerts;
using Keel.Domain;
using Keel.Domain.Recurring;

namespace Keel.Desktop.ViewModels.Bills;

/// <summary>A past occurrence in the detail panel's amount history.</summary>
/// <param name="Date">Date.</param>
/// <param name="Amount">Signed amount.</param>
/// <param name="Currency">Currency.</param>
/// <param name="TransactionId">Ledger transaction.</param>
public sealed record BillHistoryPoint(DateOnly Date, long Amount, string Currency, Guid? TransactionId)
{
    /// <summary>"Sep 3, 2026".</summary>
    public string DateText => BillsFormat.LongDate(Date);

    /// <summary>Magnitude text.</summary>
    public string AmountText => LedgerText.Money(Math.Abs(Amount), Currency);
}

/// <summary>
/// The Bills detail panel (PRD 9.6): what the item is, how it was detected, its amount history (chart
/// and table), its alerts, and which actions apply in its status.
/// </summary>
public sealed class BillDetailViewModel
{
    /// <summary>Creates the panel.</summary>
    public BillDetailViewModel(RecurringItemDetailDto detail, IReadOnlyList<AlertItemViewModel> alerts, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(detail);
        Detail = detail;
        Row = new BillItemViewModel(detail.Item, today);
        Alerts = alerts;
        History = detail.History.Select(h => new BillHistoryPoint(h.Date, h.Amount.Amount, h.Amount.Currency, h.TransactionId)).ToList();
        var score = detail.Scores.FirstOrDefault(s => s.Cadence == Item.Cadence);
        var gaps = Math.Max(0, detail.History.Count(h => h.Date >= RecurringWindowStart(today)) - 1);
        ExplanationText = score is null || gaps == 0
            ? Strings.Bills_ExplainManual
            : LedgerText.Format(Strings.Bills_ExplainDetected, (int)Math.Round(score.Fraction * gaps), gaps, BillsFormat.Cadence(Item.Cadence).ToLower(CultureInfo.CurrentCulture), Row.ConfidenceText);
        var setAside = RecurringMath.MonthlySetAside(Item.ExpectedAmount.Amount, Item.Cadence);
        TargetText = LedgerText.Format(Strings.Bills_TargetHint, LedgerText.Money(setAside, Item.ExpectedAmount.Currency));
        PriceChanges = PriceChangesOf(detail).ToList();
    }

    /// <summary>The loaded detail.</summary>
    public RecurringItemDetailDto Detail { get; }

    /// <summary>The item.</summary>
    public RecurringItemDto Item => Detail.Item;

    /// <summary>Row texts for the item.</summary>
    public BillItemViewModel Row { get; }

    /// <summary>Past occurrences, oldest first.</summary>
    public IReadOnlyList<BillHistoryPoint> History { get; }

    /// <summary>Newest first, for the table under the chart.</summary>
    public IReadOnlyList<BillHistoryPoint> RecentHistory => History.Reverse().Take(6).ToList();

    /// <summary>Whether there is any history.</summary>
    public bool HasHistory => History.Count > 0;

    /// <summary>Price changes of this item.</summary>
    public IReadOnlyList<PriceChangeRow> PriceChanges { get; }

    /// <summary>This item's alerts.</summary>
    public IReadOnlyList<AlertItemViewModel> Alerts { get; }

    /// <summary>Whether the item has alerts.</summary>
    public bool HasAlerts => Alerts.Count > 0;

    /// <summary>"5 of 5 gaps were monthly (94% confidence)" or the manual-item line.</summary>
    public string ExplanationText { get; }

    /// <summary>"Sets aside $8.34 a month" (F-REC-4).</summary>
    public string TargetText { get; }

    /// <summary>"Last paid Sep 3, 2026" or "Not seen yet".</summary>
    public string LastSeenText => Item.LastSeenDate == DateOnly.MinValue || Item.LastSeenDate > Item.NextExpectedDate
        ? Strings.Bills_NeverSeen
        : LedgerText.Format(Strings.Bills_LastSeen, BillsFormat.LongDate(Item.LastSeenDate));

    /// <summary>"Next Oct 1, 2026 (in 7 days)".</summary>
    public string NextText => LedgerText.Format(Strings.Bills_NextDue, BillsFormat.LongDate(Item.NextExpectedDate), Row.DueText);

    /// <summary>Detected, awaiting confirmation.</summary>
    public bool CanConfirm => Item.Status == RecurringStatus.Detected;

    /// <summary>Active or detected.</summary>
    public bool CanPause => Item.Status is RecurringStatus.Active or RecurringStatus.Detected;

    /// <summary>Paused or ended.</summary>
    public bool CanResume => Item.Status is RecurringStatus.Paused or RecurringStatus.Ended;

    /// <summary>Anything but dismissed.</summary>
    public bool CanDismiss => Item.Status != RecurringStatus.Dismissed;

    /// <summary>Dismissed: detection can be turned back on for the payee.</summary>
    public bool CanReenable => Item.Status == RecurringStatus.Dismissed;

    /// <summary>Has a category (a target needs one).</summary>
    public bool CanCreateTarget => Item.CategoryId is not null;

    /// <summary>No category: explains why the target button is disabled.</summary>
    public bool NeedsCategoryForTarget => Item.CategoryId is null;

    /// <summary>A scheduled transaction was created from this item.</summary>
    public bool HasSchedule => Item.ScheduledTransactionId is not null;

    /// <summary>No schedule yet.</summary>
    public bool CanCreateSchedule => Item.ScheduledTransactionId is null && Item.AccountId is not null;

    private static DateOnly RecurringWindowStart(DateOnly today) => today.AddMonths(-RecurringDetector.LookbackMonths);

    /// <summary>Consecutive occurrences whose amount changed (by more than a cent), oldest first.</summary>
    public static IEnumerable<PriceChangeRow> PriceChangesOf(RecurringItemDetailDto detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        for (var i = 1; i < detail.History.Count; i++)
        {
            var before = Math.Abs(detail.History[i - 1].Amount.Amount);
            var after = Math.Abs(detail.History[i].Amount.Amount);
            if (before != after)
            {
                yield return new PriceChangeRow(detail.Item.Id, detail.Item.PayeeName, detail.History[i].Date, before, after, detail.History[i].Amount.Currency);
            }
        }
    }
}
