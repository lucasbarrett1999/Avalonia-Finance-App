using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Ledger;
using Keel.Application.Scheduling;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.ViewModels.Bills;

/// <summary>An upcoming scheduled instance: a ghost row in the register or a line of the startup prompt.</summary>
public sealed partial class ScheduledInstanceRowViewModel(ScheduledInstanceDto instance) : ObservableObject
{
    /// <summary>The instance.</summary>
    public ScheduledInstanceDto Instance { get; } = instance;

    /// <summary>Schedule.</summary>
    public Guid ScheduledId => Instance.ScheduledId;

    /// <summary>Date.</summary>
    public DateOnly Date => Instance.Date;

    /// <summary>Short date.</summary>
    public string DateText => Instance.Date.ToString("d", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>"Wed, Sep 24".</summary>
    public string DayText => BillsFormat.DayDate(Instance.Date);

    /// <summary>Payee, or "Transfer: Savings".</summary>
    public string Payee => Instance.TransferAccountName is { } other ? LedgerText.TransferPayee(other) : Instance.PayeeName;

    /// <summary>Category.</summary>
    public string Category => Instance.CategoryName ?? (Instance.TransferAccountName is null ? Strings.Register_Uncategorized : string.Empty);

    /// <summary>Memo.</summary>
    public string Memo => Instance.Memo ?? string.Empty;

    /// <summary>Outflow text.</summary>
    public string Outflow => Instance.Amount.Amount < 0 ? LedgerText.Money(-Instance.Amount.Amount, Instance.Amount.Currency) : string.Empty;

    /// <summary>Inflow text.</summary>
    public string Inflow => Instance.Amount.Amount > 0 ? LedgerText.Money(Instance.Amount.Amount, Instance.Amount.Currency) : string.Empty;

    /// <summary>Signed amount text for single-column lists.</summary>
    public string AmountText => (Instance.Amount.Amount > 0 ? "+" : string.Empty) + LedgerText.Money(Math.Abs(Instance.Amount.Amount), Instance.Amount.Currency);

    /// <summary>Due before today and not entered.</summary>
    public bool IsOverdue => Instance.IsOverdue;

    /// <summary>"Scheduled", "Due today", "Overdue".</summary>
    public string StateText => Instance.IsOverdue ? Strings.Schedule_Overdue
        : Instance.Date == DateOnly.FromDateTime(DateTime.Today) ? Strings.Bills_DueToday
        : Instance.AutoEnter ? Strings.Schedule_AutoEntered : Strings.Schedule_Upcoming;

    /// <summary>The next instance of its schedule: the only one that can be entered or skipped.</summary>
    [ObservableProperty]
    public partial bool IsNext { get; set; } = instance.IsNext;

    /// <summary>Screen-reader text.</summary>
    public string AutomationName => LedgerText.Format(Strings.Schedule_RowAutomation, DateText, Payee, AmountText, StateText);
}

/// <summary>
/// The startup prompt (F-ACC-6, "prompt" mode): scheduled instances due today or earlier whose schedule
/// is not auto-entered. Each can be entered or skipped (in order per schedule); "Enter all" enters every
/// remaining one; "Later" leaves them for the next start or day change.
/// </summary>
public sealed partial class ScheduledPromptViewModel : DialogViewModel
{
    private readonly IScheduledTransactionService _scheduled;

    /// <summary>Creates the prompt.</summary>
    public ScheduledPromptViewModel(IScheduledTransactionService scheduled, IReadOnlyList<ScheduledInstanceDto> due)
    {
        ArgumentNullException.ThrowIfNull(due);
        _scheduled = scheduled;
        foreach (var row in due.Select(d => new ScheduledInstanceRowViewModel(d)))
        {
            Rows.Add(row);
        }
    }

    /// <inheritdoc />
    public override string Title => Strings.Schedule_PromptTitle;

    /// <summary>Due instances, oldest first.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ScheduledInstanceRowViewModel> Rows { get; } = [];

    /// <summary>"3 scheduled transactions are due."</summary>
    public string Message => LedgerText.Format(Rows.Count == 1 ? Strings.Schedule_PromptOne : Strings.Schedule_PromptMany, Rows.Count);

    /// <summary>Transactions entered from this prompt.</summary>
    public int EnteredCount { get; private set; }

    /// <summary>Enters one instance.</summary>
    [RelayCommand]
    public Task EnterAsync(ScheduledInstanceRowViewModel? row) => ActAsync(row, enter: true);

    /// <summary>Skips one instance.</summary>
    [RelayCommand]
    public Task SkipAsync(ScheduledInstanceRowViewModel? row) => ActAsync(row, enter: false);

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        foreach (var row in Rows.ToList())
        {
            if (!await DoAsync(row, enter: true))
            {
                return false;
            }
        }

        return true;
    }

    private async Task ActAsync(ScheduledInstanceRowViewModel? row, bool enter)
    {
        if (row is null || !row.IsNext)
        {
            return;
        }

        if (await DoAsync(row, enter) && Rows.Count == 0)
        {
            Close(true);
        }
    }

    private async Task<bool> DoAsync(ScheduledInstanceRowViewModel row, bool enter)
    {
        try
        {
            if (enter)
            {
                await _scheduled.EnterInstanceAsync(row.ScheduledId, row.Date, CancellationToken.None);
                EnteredCount++;
            }
            else
            {
                await _scheduled.SkipInstanceAsync(row.ScheduledId, row.Date, CancellationToken.None);
            }
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            return false;
        }

        Rows.Remove(row);
        if (Rows.FirstOrDefault(r => r.ScheduledId == row.ScheduledId) is { } next)
        {
            next.IsNext = true;
        }

        OnPropertyChanged(nameof(Message));
        return true;
    }
}
