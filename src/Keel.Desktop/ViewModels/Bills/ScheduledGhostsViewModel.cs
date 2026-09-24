using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Accounts;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Application.Payees;
using Keel.Application.Scheduling;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Register;

namespace Keel.Desktop.ViewModels.Bills;

/// <summary>Opens the scheduled-transaction editor with fresh account and category lists.</summary>
public sealed class ScheduleEditorLauncher(
    IScheduledTransactionService scheduled,
    IPayeeService payees,
    IAccountService accounts,
    ICategoryService categories,
    DialogService dialogs,
    StatusService status,
    TimeProvider time)
{
    /// <summary>The service behind the editor.</summary>
    public IScheduledTransactionService Scheduled => scheduled;

    /// <summary>Shows the editor for a new schedule (optionally pre-filled) or an existing one; returns the dialog after it closed, or null when cancelled.</summary>
    public async Task<ScheduledTransactionEditorViewModel?> ShowAsync(ScheduledDraft? draft = null, Guid? existingId = null)
    {
        var accountList = await accounts.GetAccountsAsync(includeClosed: false, CancellationToken.None);
        var categoryList = await categories.GetCategoriesAsync(includeHidden: false, CancellationToken.None);
        var existing = existingId is { } id ? await scheduled.GetByIdAsync(id, CancellationToken.None) : null;
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var dialog = new ScheduledTransactionEditorViewModel(
            scheduled,
            payees,
            accountList.Select(AccountOption.From).ToList(),
            categoryList.Where(c => !c.IsCreditCardPayment).Select(CategoryOption.From).ToList(),
            today,
            existing,
            draft);
        if (!await dialogs.ShowAsync(dialog))
        {
            return null;
        }

        status.Show(dialog.Deleted ? Strings.Schedule_Deleted : LedgerText.Format(Strings.Schedule_Saved, dialog.Result?.PayeeName, dialog.Result?.Schedule), offerUndo: true);
        return dialog;
    }
}

/// <summary>
/// Ghost rows of the register (PRD 9.4, F-ACC-6): upcoming scheduled instances of the shown account (or
/// all accounts) for the next <see cref="DaysAhead"/> days plus any overdue ones, italic above the grid,
/// with "Enter now", "Skip" and "Edit" on each schedule's next instance.
/// </summary>
public sealed partial class ScheduledGhostsViewModel(ScheduleEditorLauncher launcher, StatusService status, TimeProvider time) : ObservableObject
{
    /// <summary>How far ahead ghost rows are shown.</summary>
    public const int DaysAhead = 31;

    private Guid? _accountId;
    private int _version;

    /// <summary>Upcoming instances, oldest first.</summary>
    public ObservableCollection<ScheduledInstanceRowViewModel> Rows { get; } = [];

    /// <summary>Whether there is anything upcoming.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    public partial bool HasRows { get; private set; }

    /// <summary>Whether the rows are expanded.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    /// <summary>"Scheduled (3)".</summary>
    public string HeaderText => LedgerText.Format(Strings.Schedule_GhostHeader, Rows.Count);

    /// <summary>The current load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the rows for an account (null for all accounts).</summary>
    public Task LoadAsync(Guid? accountId)
    {
        _accountId = accountId;
        return Loading = LoadCoreAsync();
    }

    /// <summary>Reloads for the same account.</summary>
    public Task ReloadAsync() => Loading = LoadCoreAsync();

    /// <summary>Enters the instance now (on its own date).</summary>
    [RelayCommand]
    public async Task EnterAsync(ScheduledInstanceRowViewModel? row)
    {
        if (row is not { IsNext: true })
        {
            return;
        }

        await RunAsync(() => launcher.Scheduled.EnterInstanceAsync(row.ScheduledId, row.Date, CancellationToken.None), LedgerText.Format(Strings.Schedule_Entered, row.Payee));
    }

    /// <summary>Skips the instance.</summary>
    [RelayCommand]
    public async Task SkipAsync(ScheduledInstanceRowViewModel? row)
    {
        if (row is not { IsNext: true })
        {
            return;
        }

        await RunAsync(() => launcher.Scheduled.SkipInstanceAsync(row.ScheduledId, row.Date, CancellationToken.None), LedgerText.Format(Strings.Schedule_Skipped, row.Payee));
    }

    /// <summary>Edits the schedule behind a row.</summary>
    [RelayCommand]
    public async Task EditAsync(ScheduledInstanceRowViewModel? row)
    {
        if (row is not null && await launcher.ShowAsync(existingId: row.ScheduledId) is not null)
        {
            await ReloadAsync();
        }
    }

    /// <summary>Creates a schedule for the shown account.</summary>
    [RelayCommand]
    public async Task NewScheduleAsync()
    {
        if (await launcher.ShowAsync(new ScheduledDraft(_accountId, null, 0, null, null, null)) is not null)
        {
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    private async Task RunAsync(Func<Task> action, string done)
    {
        try
        {
            await action();
            status.Show(done, offerUndo: true);
        }
        catch (LedgerValidationException ex)
        {
            status.Show(LedgerText.Error(ex.Error), isError: true);
        }
        catch (InvalidOperationException ex)
        {
            status.Show(ex.Message, isError: true);
        }

        await ReloadAsync();
    }

    private async Task LoadCoreAsync()
    {
        var version = ++_version;
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        IReadOnlyList<ScheduledInstanceDto> instances;
        try
        {
            instances = await launcher.Scheduled.GetUpcomingAsync(today.AddYears(-1), today.AddDays(DaysAhead), _accountId, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            instances = [];
        }

        if (version != _version)
        {
            return;
        }

        Rows.Clear();
        foreach (var instance in instances)
        {
            Rows.Add(new ScheduledInstanceRowViewModel(instance));
        }

        HasRows = Rows.Count > 0;
        OnPropertyChanged(nameof(HeaderText));
    }
}
