using CommunityToolkit.Mvvm.Input;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Bills;

/// <summary>One amount on a calendar day: a paid or expected recurring occurrence, or a scheduled instance.</summary>
/// <param name="ItemId">Recurring item, if any.</param>
/// <param name="ScheduledId">Scheduled transaction, if any.</param>
/// <param name="Label">Payee.</param>
/// <param name="Amount">Signed amount.</param>
/// <param name="Currency">Currency.</param>
/// <param name="IsPaid">A past occurrence found in the ledger (otherwise expected).</param>
/// <param name="Open">Selects the item (or edits the schedule).</param>
public sealed record BillsCalendarEntry(Guid? ItemId, Guid? ScheduledId, string Label, long Amount, string Currency, bool IsPaid, IRelayCommand<BillsCalendarEntry> Open)
{
    /// <summary>"$15.49" or "+$2,000.00".</summary>
    public string AmountText => (Amount > 0 ? "+" : string.Empty) + LedgerText.Money(Math.Abs(Amount), Currency);

    /// <summary>Income.</summary>
    public bool IsIncome => Amount > 0;

    /// <summary>A scheduled transaction's instance.</summary>
    public bool IsScheduled => ScheduledId is not null;

    /// <summary>Screen-reader and tooltip text ("Netflix, $15.49, paid").</summary>
    public string Description => LedgerText.Format(IsPaid ? Strings.Bills_CalendarPaid : IsScheduled ? Strings.Bills_CalendarScheduled : Strings.Bills_CalendarExpected, Label, AmountText);
}

/// <summary>One cell of the month grid.</summary>
/// <param name="Date">Date.</param>
/// <param name="IsCurrentMonth">In the month shown (other days are dimmed).</param>
/// <param name="IsToday">Today.</param>
/// <param name="Entries">What falls on the day (at most <see cref="MaxEntries"/> shown).</param>
/// <param name="Currency">Currency.</param>
public sealed record BillsCalendarDay(DateOnly Date, bool IsCurrentMonth, bool IsToday, IReadOnlyList<BillsCalendarEntry> Entries, string Currency)
{
    /// <summary>Entries shown in a cell.</summary>
    public const int MaxEntries = 3;

    /// <summary>Day number.</summary>
    public string DayText => Date.Day.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>The entries that fit in the cell.</summary>
    public IReadOnlyList<BillsCalendarEntry> Shown => Entries.Take(MaxEntries).ToList();

    /// <summary>"+2 more".</summary>
    public string MoreText => Entries.Count > MaxEntries ? LedgerText.Format(Strings.Bills_CalendarMore, Entries.Count - MaxEntries) : string.Empty;

    /// <summary>Whether entries were cut off.</summary>
    public bool HasMore => Entries.Count > MaxEntries;

    /// <summary>Net outflow of the day (shown under the day number when there is something).</summary>
    public string TotalText => Entries.Count == 0 ? string.Empty : LedgerText.Money(Entries.Sum(e => e.Amount), Currency);

    /// <summary>Screen-reader text for the day.</summary>
    public string AutomationName => Entries.Count == 0
        ? BillsFormat.LongDate(Date)
        : BillsFormat.LongDate(Date) + ": " + string.Join("; ", Entries.Select(e => e.Description));
}
