using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Accounts;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Sync;

/// <summary>
/// The desktop side of bank sync (F-TXN-3, PRD 9.1): "Sync all" with the last-sync time, one-account
/// and one-connection syncs, the scheduled sync (on start and every N hours), and the add, reconnect
/// and unlink flows. Syncs run on the thread pool; progress and results go to the non-modal status
/// strip, failures become connection states (never error dialogs).
/// </summary>
public sealed partial class SyncCoordinator : ObservableObject, IRecipient<SyncConnectionsChanged>
{
    private readonly ISyncService _sync;
    private readonly IBankCredentialsService _credentials;
    private readonly IAccountService _accounts;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private readonly TimeProvider _time;
    private DispatcherTimer? _timer;

    /// <summary>Creates the coordinator.</summary>
    public SyncCoordinator(
        ISyncService sync,
        IBankCredentialsService credentials,
        IAccountService accounts,
        DialogService dialogs,
        StatusService status,
        IMessenger messenger,
        IBrowserLauncher browser,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(messenger);
        _sync = sync;
        _credentials = credentials;
        _accounts = accounts;
        _dialogs = dialogs;
        _status = status;
        _time = time;
        Browser = browser;
        messenger.Register(this);
    }

    /// <summary>Raised on the UI thread after <see cref="Connections"/> reloads.</summary>
    public event EventHandler? ConnectionsRefreshed;

    /// <summary>The browser launcher (tests replace it).</summary>
    public IBrowserLauncher Browser { get; set; }

    /// <summary>Connections as of the last refresh.</summary>
    public IReadOnlyList<SyncConnectionDto> Connections { get; private set; } = [];

    /// <summary>The latest refresh (tests await it).</summary>
    public Task Refreshing { get; private set; } = Task.CompletedTask;

    /// <summary>The latest sync run (tests await it).</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>The latest add, reconnect or unlink flow (tests await it).</summary>
    public Task Flow { get; private set; } = Task.CompletedTask;

    /// <summary>Whether a sync is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncAllToolTip))]
    [NotifyCanExecuteChangedFor(nameof(SyncAllCommand))]
    public partial bool IsSyncing { get; private set; }

    /// <summary>Whether any connection exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncAllToolTip))]
    [NotifyCanExecuteChangedFor(nameof(SyncAllCommand))]
    public partial bool HasConnections { get; private set; }

    /// <summary>"Synced 5 min ago", from the most recent successful sync of any connection.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncAllToolTip))]
    public partial string LastSyncText { get; private set; } = Strings.Sync_NeverSynced;

    /// <summary>Tooltip of the top-bar button.</summary>
    public string SyncAllToolTip => !HasConnections ? Strings.Shell_SyncUnavailable
        : IsSyncing ? Strings.Sync_Running
        : LedgerText.Format(Strings.Sync_AllToolTip, LastSyncText);

    /// <summary>Whether "Sync all" can run.</summary>
    public bool CanSyncAll => HasConnections && !IsSyncing;

    /// <summary>Connection of an account, if linked.</summary>
    public SyncConnectionDto? ConnectionOf(Guid accountId) =>
        Connections.FirstOrDefault(c => c.Accounts.Any(a => a.AccountId == accountId));

    /// <summary>Loads connections, applies the schedule and, when set, syncs on start.</summary>
    public async Task StartAsync()
    {
        await RefreshAsync();
        SyncSettings settings;
        try
        {
            settings = await _sync.GetSettingsAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        ApplySchedule(settings);
        if (settings.SyncOnStart && HasConnections)
        {
            await SyncAllAsync();
        }
    }

    /// <summary>Starts (or stops, for 0) the scheduled sync every <see cref="SyncSettings.IntervalHours"/> hours.</summary>
    public void ApplySchedule(SyncSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _timer?.Stop();
        _timer = null;
        if (settings.IntervalHours <= 0)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(settings.IntervalHours) };
        _timer.Tick += (_, _) =>
        {
            if (CanSyncAll)
            {
                _ = SyncAllAsync();
            }
        };
        _timer.Start();
    }

    /// <summary>The scheduled interval, or null when scheduled sync is off.</summary>
    public TimeSpan? ScheduledInterval => _timer?.Interval;

    /// <summary>Reloads the connections.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            Connections = await _sync.GetConnectionsAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            Connections = [];
        }

        HasConnections = Connections.Count > 0;
        var last = Connections.Where(c => c.LastSyncAt is not null).Select(c => c.LastSyncAt).DefaultIfEmpty().Max();
        LastSyncText = SyncText.LastSync(last, _time.GetUtcNow());
        ConnectionsRefreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Receive(SyncConnectionsChanged message) =>
        Dispatcher.UIThread.Post(() => Refreshing = RefreshAsync());

    /// <summary>Syncs every connection and reports the result in the status strip.</summary>
    [RelayCommand(CanExecute = nameof(CanSyncAll))]
    public Task SyncAllAsync() => Track(RunAsync(async progress =>
    {
        var result = await _sync.SyncAllAsync(progress, CancellationToken.None);
        return (result.Added, result.Updated, result.Removed, result.Connections.Where(c => !c.Succeeded).ToList());
    }));

    /// <summary>Syncs one connection.</summary>
    public Task SyncConnectionAsync(Guid connectionId) => Track(RunAsync(async progress =>
    {
        var result = await _sync.SyncConnectionAsync(connectionId, progress, CancellationToken.None);
        return (result.Added, result.Updated, result.Removed, result.Succeeded ? [] : new List<SyncRunResult> { result });
    }));

    /// <summary>Syncs the connection an account is linked to (register "Sync" button).</summary>
    public Task SyncAccountAsync(Guid accountId) => Track(RunAsync(async progress =>
    {
        var result = await _sync.SyncAccountAsync(accountId, progress, CancellationToken.None);
        return result is null
            ? (0, 0, 0, [])
            : (result.Added, result.Updated, result.Removed, result.Succeeded ? [] : new List<SyncRunResult> { result });
    }));

    /// <summary>The add-connection flow: provider, browser link with a waiting state, account mapping, first sync.</summary>
    public Task<bool> AddConnectionAsync()
    {
        var flow = AddConnectionCoreAsync();
        Flow = flow;
        return flow;
    }

    /// <summary>The reconnect flow (Plaid update mode) for a connection that needs a new sign-in.</summary>
    public Task<bool> ReconnectAsync(Guid connectionId)
    {
        var flow = ReconnectCoreAsync(connectionId);
        Flow = flow;
        return flow;
    }

    /// <summary>Reconnects the connection of an account (register banner).</summary>
    public Task<bool> ReconnectAccountAsync(Guid accountId) =>
        ConnectionOf(accountId) is { } connection ? ReconnectAsync(connection.Id) : Task.FromResult(false);

    /// <summary>Asks, then unlinks a connection; local transactions stay.</summary>
    public Task<bool> UnlinkAsync(SyncConnectionDto connection)
    {
        var flow = UnlinkCoreAsync(connection);
        Flow = flow;
        return flow;
    }

    /// <summary>The providers the add dialog offers, with whether their credentials are in place.</summary>
    public async Task<IReadOnlyList<ProviderChoice>> ProviderChoicesAsync()
    {
        PlaidCredentialsStatus plaid;
        bool simpleFin;
        try
        {
            plaid = await _credentials.GetPlaidStatusAsync(CancellationToken.None);
            simpleFin = await _credentials.HasSimpleFinSetupTokenAsync(CancellationToken.None);
        }
        catch (SecretStoreException)
        {
            plaid = new PlaidCredentialsStatus(false, false, PlaidEnvironment.Sandbox);
            simpleFin = false;
        }

        return _sync.Providers.Select(p => p.Kind == SyncProvider.SimpleFin
                ? new ProviderChoice(p.ProviderId, SyncText.Provider(p.Kind), Strings.AddConnection_SimpleFinDescription, simpleFin, Strings.AddConnection_SimpleFinNeedsToken)
                : new ProviderChoice(p.ProviderId, SyncText.Provider(p.Kind), LedgerText.Format(Strings.AddConnection_PlaidDescription, plaid.Environment == PlaidEnvironment.Production ? Strings.PlaidEnvironment_Production : Strings.PlaidEnvironment_Sandbox), plaid.IsComplete, Strings.AddConnection_PlaidNeedsKeys))
            .ToList();
    }

    private async Task<bool> AddConnectionCoreAsync()
    {
        var add = new AddConnectionViewModel(_sync, Browser, await ProviderChoicesAsync());
        if (!await _dialogs.ShowAsync(add) || add.Pending is not { } pending)
        {
            return false;
        }

        IReadOnlyList<AccountDto> accounts;
        try
        {
            accounts = await _accounts.GetAccountsAsync(includeClosed: false, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            accounts = [];
        }

        var mapping = new AccountMappingViewModel(_sync, pending, accounts);
        if (!await _dialogs.ShowAsync(mapping) || mapping.Result is not { } connection)
        {
            await _sync.DiscardLinkAsync(pending, CancellationToken.None);
            _status.Show(LedgerText.Format(Strings.Sync_LinkDiscarded, pending.InstitutionName));
            return false;
        }

        await RefreshAsync();
        _status.Show(LedgerText.Format(Strings.Sync_Linked, connection.InstitutionName, connection.Accounts.Count));
        await SyncConnectionAsync(connection.Id);
        return true;
    }

    private async Task<bool> ReconnectCoreAsync(Guid connectionId)
    {
        var connection = Connections.FirstOrDefault(c => c.Id == connectionId);
        var dialog = new AddConnectionViewModel(_sync, Browser, connectionId, connection?.InstitutionName ?? string.Empty);
        if (!await _dialogs.ShowAsync(dialog) || dialog.Reconnected is not { } repaired)
        {
            return false;
        }

        await RefreshAsync();
        _status.Show(LedgerText.Format(Strings.Sync_Reconnected, repaired.InstitutionName));
        await SyncConnectionAsync(repaired.Id);
        return true;
    }

    private async Task<bool> UnlinkCoreAsync(SyncConnectionDto connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var dialog = new UnlinkConnectionViewModel(_sync, connection);
        if (!await _dialogs.ShowAsync(dialog))
        {
            return false;
        }

        await RefreshAsync();
        _status.Show(LedgerText.Format(Strings.Sync_Unlinked, connection.InstitutionName));
        return true;
    }

    private Task Track(Task run)
    {
        Running = run;
        return run;
    }

    // Runs a sync with progress in the status strip; failures are reported, never thrown.
    private async Task RunAsync(Func<IProgress<SyncProgress>, Task<(int Added, int Updated, int Removed, List<SyncRunResult> Failed)>> sync)
    {
        if (IsSyncing)
        {
            return;
        }

        IsSyncing = true;
        var progress = new Progress<SyncProgress>(p => _status.Show(p.ConnectionCount > 1
            ? LedgerText.Format(Strings.Sync_ProgressMany, p.InstitutionName, p.ConnectionIndex, p.ConnectionCount, p.TransactionsReceived)
            : LedgerText.Format(Strings.Sync_Progress, p.InstitutionName, p.TransactionsReceived)));
        try
        {
            var (added, updated, removed, failed) = await sync(progress);
            await RefreshAsync();
            if (failed.Count == 0)
            {
                _status.Show(SyncText.Summary(added, updated, removed));
            }
            else
            {
                var first = failed[0];
                var message = LedgerText.Format(Strings.Sync_ConnectionFailed, first.InstitutionName, SyncText.Error(first.ErrorCode));
                if (failed.Count > 1)
                {
                    message += " " + LedgerText.Format(Strings.Sync_MoreFailed, failed.Count - 1);
                }

                _status.Show(message, isError: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            _status.Show(LedgerText.Format(Strings.Sync_Failed, ex.Message), isError: true);
        }
        finally
        {
            IsSyncing = false;
        }
    }
}

/// <summary>A provider in the add-connection dialog.</summary>
/// <param name="ProviderId">Provider id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">One line about it.</param>
/// <param name="IsReady">Whether its credentials are in place.</param>
/// <param name="NotReadyHint">What to do first when not ready.</param>
public sealed record ProviderChoice(string ProviderId, string Name, string Description, bool IsReady, string NotReadyHint);
