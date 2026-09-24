using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Accounts;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Dialogs;

/// <summary>Records a dated balance for a tracking account (F-ACC-7).</summary>
public sealed partial class RecordBalanceViewModel : DialogViewModel
{
    private readonly IBalanceSnapshotService _snapshots;
    private readonly AccountDto _account;

    /// <summary>Creates the dialog, pre-filled with today and the ledger balance.</summary>
    public RecordBalanceViewModel(IBalanceSnapshotService snapshots, AccountDto account, IReadOnlyList<BalanceSnapshotDto> history)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(history);
        _snapshots = snapshots;
        _account = account;
        Date = DateTime.Today;
        Balance = history.Count > 0 ? history[0].Balance : account.Balance.Amount;
        History = history.Take(6)
            .Select(h => new SnapshotRow(h.Date.ToString("d", CultureInfo.CurrentCulture), LedgerText.Money(h.Balance, account.Balance.Currency)))
            .ToList();
    }

    /// <inheritdoc />
    public override string Title => LedgerText.Format(Strings.RecordBalance_Title, _account.Name);

    /// <summary>Currency.</summary>
    public string Currency => _account.Balance.Currency;

    /// <summary>Recent snapshots, newest first.</summary>
    public IReadOnlyList<SnapshotRow> History { get; }

    /// <summary>Whether there is history to show.</summary>
    public bool HasHistory => History.Count > 0;

    /// <summary>Date of the balance.</summary>
    [ObservableProperty]
    public partial DateTime? Date { get; set; }

    /// <summary>Balance in minor units (signed; liabilities negative).</summary>
    [ObservableProperty]
    public partial long Balance { get; set; }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        if (Date is not { } date)
        {
            Error = Strings.Editor_ErrorDate;
            return false;
        }

        await _snapshots.RecordAsync(_account.Id, DateOnly.FromDateTime(date), Balance, BalanceSource.Manual, CancellationToken.None);
        return true;
    }
}

/// <summary>A row of snapshot history.</summary>
public sealed record SnapshotRow(string Date, string Balance);
