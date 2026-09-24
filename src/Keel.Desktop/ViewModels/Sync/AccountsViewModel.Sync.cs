using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Accounts;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Sync;
using Keel.Domain;

namespace Keel.Desktop.ViewModels;

/// <summary>
/// The register's bank-sync parts (PRD 9.4, principle 3): the header "Sync" button for linked
/// accounts and the connection state shown on the account with its fix action (Reconnect).
/// </summary>
public sealed partial class AccountsViewModel
{
    private SyncCoordinator? _sync;

    /// <summary>Whether the account is linked to a bank connection.</summary>
    public bool IsLinked => Account is { SyncStatus: not null, IsClosed: false };

    /// <summary>Whether the connection needs attention (the banner shows).</summary>
    public bool HasConnectionProblem => Account?.SyncStatus is SyncStatus.NeedsReauth or SyncStatus.Error;

    /// <summary>Whether the fix is a new sign-in (Reconnect).</summary>
    public bool NeedsReconnect => Account?.SyncStatus == SyncStatus.NeedsReauth;

    /// <summary>The banner text for a connection problem.</summary>
    public string ConnectionProblemText => Account?.SyncStatus switch
    {
        SyncStatus.NeedsReauth => Strings.Register_ConnectionNeedsReauth,
        SyncStatus.Error => _sync?.ConnectionOf(Account.Id) is { LastError: { } code }
            ? LedgerText.Format(Strings.Register_ConnectionErrorWithReason, SyncText.Error(code))
            : Strings.Register_ConnectionError,
        _ => string.Empty,
    };

    /// <summary>"Synced 5 min ago" for the linked account.</summary>
    public string LinkedSyncText => Account is { } a && _sync?.ConnectionOf(a.Id) is { } c
        ? LedgerText.Format(Strings.Register_LinkedTo, c.InstitutionName, SyncText.LastSync(c.LastSyncAt, TimeProvider.System.GetUtcNow()))
        : string.Empty;

    /// <summary>Whether a sync is running (the Sync button is disabled).</summary>
    public bool IsSyncing => _sync?.IsSyncing ?? false;

    private void AttachSync(SyncCoordinator sync)
    {
        _sync = sync;
        sync.ConnectionsRefreshed += (_, _) =>
        {
            if (AccountId is not null)
            {
                Loading = LoadAsync(showSpinner: false);
            }

            NotifySync();
        };
        sync.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SyncCoordinator.IsSyncing))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    OnPropertyChanged(nameof(IsSyncing));
                    SyncAccountCommand.NotifyCanExecuteChanged();
                });
            }
        };
    }

    partial void OnAccountChanged(AccountDto? value) => NotifySync();

    private void NotifySync()
    {
        OnPropertyChanged(nameof(IsLinked));
        OnPropertyChanged(nameof(HasConnectionProblem));
        OnPropertyChanged(nameof(NeedsReconnect));
        OnPropertyChanged(nameof(ConnectionProblemText));
        OnPropertyChanged(nameof(LinkedSyncText));
        SyncAccountCommand.NotifyCanExecuteChanged();
    }

    private bool CanSyncAccount() => IsLinked && !IsSyncing;

    /// <summary>Syncs this account's connection (header "Sync" button).</summary>
    [RelayCommand(CanExecute = nameof(CanSyncAccount))]
    private Task SyncAccountAsync() => Account is { } a && _sync is { } sync ? sync.SyncAccountAsync(a.Id) : Task.CompletedTask;

    /// <summary>Reconnects this account's connection (banner "Reconnect").</summary>
    [RelayCommand]
    private Task ReconnectAccountAsync() => Account is { } a && _sync is { } sync ? sync.ReconnectAccountAsync(a.Id) : Task.CompletedTask;
}
