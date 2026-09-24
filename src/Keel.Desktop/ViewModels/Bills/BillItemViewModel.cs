using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Recurring;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Bills;

/// <summary>A recurring item as a Bills list row (sortable raw values plus display text).</summary>
public sealed partial class BillItemViewModel : ObservableObject
{
    /// <summary>Creates the row.</summary>
    public BillItemViewModel(RecurringItemDto item, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
        Today = today;
    }

    /// <summary>The item.</summary>
    public RecurringItemDto Item { get; }

    /// <summary>Today (for relative due text).</summary>
    public DateOnly Today { get; }

    /// <summary>Id.</summary>
    public Guid Id => Item.Id;

    /// <summary>Payee (sort key and text).</summary>
    public string PayeeName => Item.PayeeName;

    /// <summary>Next expected date (sort key).</summary>
    public DateOnly NextDate => Item.NextExpectedDate;

    /// <summary>"Oct 1".</summary>
    public string NextDateText => Item.Status is RecurringStatus.Active or RecurringStatus.Detected ? BillsFormat.ShortDate(Item.NextExpectedDate) : "—";

    /// <summary>"In 5 days" (or empty for inactive items).</summary>
    public string DueText => Item.Status is RecurringStatus.Active or RecurringStatus.Detected ? BillsFormat.Due(Item.NextExpectedDate, Today) : string.Empty;

    /// <summary>Overdue: the next date passed without the charge arriving.</summary>
    public bool IsOverdue => Item.Status == RecurringStatus.Active && Item.NextExpectedDate < Today;

    /// <summary>Signed amount (sort key: largest bills first when descending by magnitude).</summary>
    public long Amount => Item.ExpectedAmount.Amount;

    /// <summary>Magnitude of the amount (sort key).</summary>
    public long Magnitude => Math.Abs(Item.ExpectedAmount.Amount);

    /// <summary>"$15.49" or "+$2,000.00" for income; "~" marks variable amounts.</summary>
    public string AmountText => (Item.IsVariableAmount ? "~" : string.Empty)
        + (Item.ExpectedAmount.Amount > 0 ? "+" : string.Empty)
        + LedgerText.Money(Math.Abs(Item.ExpectedAmount.Amount), Item.ExpectedAmount.Currency);

    /// <summary>Inflow (paycheck).</summary>
    public bool IsIncome => Item.ExpectedAmount.Amount > 0;

    /// <summary>Monthly equivalent text.</summary>
    public string MonthlyText => LedgerText.Money(Math.Abs(Item.MonthlyEquivalent.Amount), Item.MonthlyEquivalent.Currency);

    /// <summary>Yearly equivalent text.</summary>
    public string YearlyText => LedgerText.Money(Math.Abs(Keel.Domain.Recurring.RecurringMath.Annualized(Item.ExpectedAmount.Amount, Item.Cadence)), Item.ExpectedAmount.Currency);

    /// <summary>Cadence order (sort key).</summary>
    public int CadenceOrder => (int)Item.Cadence;

    /// <summary>"Monthly".</summary>
    public string CadenceText => BillsFormat.Cadence(Item.Cadence);

    /// <summary>"Every month on the 1st".</summary>
    public string Schedule => Item.Schedule;

    /// <summary>Category (sort key and text).</summary>
    public string CategoryName => Item.CategoryName ?? Strings.Register_Uncategorized;

    /// <summary>Whether the item has no category.</summary>
    public bool IsUncategorized => Item.CategoryId is null;

    /// <summary>Account (sort key and text).</summary>
    public string AccountName => Item.AccountName ?? Strings.Bills_AnyAccount;

    /// <summary>Status (sort key and text).</summary>
    public string StatusName => BillsFormat.Status(Item.Status);

    /// <summary>Status.</summary>
    public RecurringStatus Status => Item.Status;

    /// <summary>Unconfirmed detection (shows the confirm hint).</summary>
    public bool IsDetected => Item.Status == RecurringStatus.Detected;

    /// <summary>Active.</summary>
    public bool IsActive => Item.Status == RecurringStatus.Active;

    /// <summary>Paused, ended or dismissed (dimmed).</summary>
    public bool IsInactive => Item.Status is RecurringStatus.Paused or RecurringStatus.Ended or RecurringStatus.Dismissed;

    /// <summary>Subscription.</summary>
    public bool IsSubscription => Item.IsSubscription;

    /// <summary>"Subscription", "Bill" or "Income".</summary>
    public string KindText => IsIncome ? Strings.Bills_KindIncome : IsSubscription ? Strings.Bills_KindSubscription : Strings.Bills_KindBill;

    /// <summary>"94% confidence".</summary>
    public string ConfidenceText => LedgerText.Format(Strings.Bills_Confidence, Item.Confidence.ToString("P0", CultureInfo.CurrentCulture));

    /// <summary>Screen-reader summary.</summary>
    public string AutomationName => LedgerText.Format(Strings.Bills_RowAutomation, PayeeName, AmountText, CadenceText, NextDateText, StatusName);

    /// <summary>Selected in the list (detail panel shown).</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>One price change of a subscription (the Subscriptions tab history).</summary>
/// <param name="ItemId">Item.</param>
/// <param name="PayeeName">Payee.</param>
/// <param name="Date">Date of the first charge at the new price.</param>
/// <param name="Before">Previous price (positive).</param>
/// <param name="After">New price (positive).</param>
/// <param name="Currency">Currency.</param>
public sealed record PriceChangeRow(Guid ItemId, string PayeeName, DateOnly Date, long Before, long After, string Currency)
{
    /// <summary>"Sep 3, 2026".</summary>
    public string DateText => BillsFormat.LongDate(Date);

    /// <summary>"$15.49 → $17.99".</summary>
    public string ChangeText => LedgerText.Format(Strings.Bills_PriceChange, LedgerText.Money(Before, Currency), LedgerText.Money(After, Currency));

    /// <summary>"+16%" or "−10%".</summary>
    public string PercentText => Before == 0 ? string.Empty
        : ((double)(After - Before) / Before).ToString("+0%;−0%", CultureInfo.CurrentCulture);

    /// <summary>Price went up.</summary>
    public bool IsIncrease => After > Before;
}
