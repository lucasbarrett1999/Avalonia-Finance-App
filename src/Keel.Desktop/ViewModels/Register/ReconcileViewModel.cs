using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Register;

/// <summary>
/// The reconcile bar of an account register (F-ACC-3): the user enters the statement date and
/// balance, sees the difference to the cleared balance, clears or unclears rows (C) until it is
/// zero, then finishes, optionally recording a balance adjustment for what is left.
/// </summary>
public sealed partial class ReconcileViewModel : ObservableObject
{
    private readonly ITransactionService _transactions;
    private readonly Guid _accountId;
    private readonly Func<ReconciliationResult, Task> _finished;
    private readonly Action _closed;
    private int _refreshVersion;

    /// <summary>Creates the bar with the statement balance pre-filled with the cleared balance.</summary>
    public ReconcileViewModel(ITransactionService transactions, Guid accountId, string currency, long clearedBalance, Func<ReconciliationResult, Task> finished, Action closed)
    {
        _transactions = transactions;
        _accountId = accountId;
        _finished = finished;
        _closed = closed;
        Currency = currency;
        StatementDate = DateTime.Today;
        StatementBalance = clearedBalance;
        ClearedBalance = clearedBalance;
    }

    /// <summary>Currency of the account.</summary>
    public string Currency { get; }

    /// <summary>Statement date.</summary>
    [ObservableProperty]
    public partial DateTime? StatementDate { get; set; }

    /// <summary>Statement balance in minor units.</summary>
    [ObservableProperty]
    public partial long StatementBalance { get; set; }

    /// <summary>Cleared balance as of the statement date.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClearedText))]
    public partial long ClearedBalance { get; private set; }

    /// <summary>Statement minus cleared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DifferenceText), nameof(IsBalanced))]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    public partial long Difference { get; private set; }

    /// <summary>Number of uncleared transactions up to the statement date.</summary>
    [ObservableProperty]
    public partial int UnclearedCount { get; private set; }

    /// <summary>Error shown in the bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    /// <summary>Whether <see cref="Error"/> is set.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Cleared balance text.</summary>
    public string ClearedText => LedgerText.Money(ClearedBalance, Currency);

    /// <summary>Difference text.</summary>
    public string DifferenceText => LedgerText.Money(Difference, Currency);

    /// <summary>Whether the difference is zero.</summary>
    public bool IsBalanced => Difference == 0;

    /// <summary>Recomputes cleared balance and difference (after edits, C toggles, or input changes).</summary>
    public async Task RefreshAsync()
    {
        if (StatementDate is not { } date)
        {
            return;
        }

        var version = ++_refreshVersion;
        var status = await _transactions.GetReconciliationStatusAsync(_accountId, DateOnly.FromDateTime(date), StatementBalance, CancellationToken.None);
        if (version != _refreshVersion)
        {
            return;
        }

        ClearedBalance = status.ClearedBalance;
        Difference = status.Difference;
        UnclearedCount = status.UnclearedCount;
    }

    partial void OnStatementBalanceChanged(long value) => _ = RefreshAsync();

    partial void OnStatementDateChanged(DateTime? value) => _ = RefreshAsync();

    private bool CanFinish() => IsBalanced;

    [RelayCommand(CanExecute = nameof(CanFinish))]
    private Task FinishAsync() => FinishCoreAsync(createAdjustment: false);

    [RelayCommand]
    private Task FinishWithAdjustmentAsync() => FinishCoreAsync(createAdjustment: true);

    [RelayCommand]
    private void Cancel() => _closed();

    private async Task FinishCoreAsync(bool createAdjustment)
    {
        if (StatementDate is not { } date)
        {
            Error = Strings.Editor_ErrorDate;
            return;
        }

        try
        {
            Error = null;
            var result = await _transactions.FinishReconciliationAsync(
                new FinishReconciliationRequest(_accountId, DateOnly.FromDateTime(date), StatementBalance, createAdjustment), CancellationToken.None);
            await _finished(result);
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
        }
    }
}
