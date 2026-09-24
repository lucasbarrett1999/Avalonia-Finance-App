using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Sync;

/// <summary>
/// Settings → Connections (F-SET-3, PRD 9.9): the connection list (institution, accounts, last sync,
/// health, Reconnect, Sync now, Unlink), provider credentials (entered masked, never shown again;
/// Replace to change), the Plaid environment, the BYO-keys warning (PRD 10), the secret store in use
/// with the Linux fallback warning, and the sync schedule.
/// </summary>
public sealed partial class ConnectionsSettingsViewModel : ViewModelBase
{
    private readonly ISyncService _sync;
    private readonly IBankCredentialsService _credentials;
    private readonly StatusService _status;
    private bool _loadingSettings;

    /// <summary>Creates the section and starts loading it.</summary>
    public ConnectionsSettingsViewModel(ISyncService sync, IBankCredentialsService credentials, SyncCoordinator coordinator, StatusService status)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _sync = sync;
        _credentials = credentials;
        _status = status;
        Coordinator = coordinator;
        Environments =
        [
            new(PlaidEnvironment.Sandbox, Strings.PlaidEnvironment_Sandbox),
            new(PlaidEnvironment.Production, Strings.PlaidEnvironment_Production),
        ];
        SelectedEnvironment = Environments[0];
        Intervals = SyncSettings.IntervalChoices
            .Select(h => new Choice<int>(h, h == 0 ? Strings.Connections_IntervalOff : LedgerText.Format(h == 1 ? Strings.Connections_IntervalHour : Strings.Connections_IntervalHours, h)))
            .ToList();
        SelectedInterval = Intervals.First(i => i.Value == SyncSettings.DefaultIntervalHours);
        coordinator.ConnectionsRefreshed += (_, _) => RebuildConnections();
        Loading = LoadAsync();
    }

    /// <summary>The coordinator (flows and sync commands).</summary>
    public SyncCoordinator Coordinator { get; }

    /// <summary>The latest load (tests await it).</summary>
    public Task Loading { get; private set; }

    /// <summary>Connections.</summary>
    public ObservableCollection<ConnectionItemViewModel> Connections { get; } = [];

    /// <summary>Whether any connection exists (else the designed empty state).</summary>
    [ObservableProperty]
    public partial bool HasConnections { get; private set; }

    /// <summary>Loading.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>A load or save problem.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Whether <see cref="ErrorMessage"/> is set.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Secret store backend name.</summary>
    [ObservableProperty]
    public partial string StoreText { get; private set; } = string.Empty;

    /// <summary>Whether the Linux encrypted-file fallback is in use (warning shown).</summary>
    [ObservableProperty]
    public partial bool IsWeakerFallback { get; private set; }

    /// <summary>Plaid client id stored.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowClientIdEditor), nameof(PlaidKeysComplete))]
    public partial bool HasClientId { get; private set; }

    /// <summary>Plaid secret stored.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSecretEditor), nameof(PlaidKeysComplete))]
    public partial bool HasSecret { get; private set; }

    /// <summary>SimpleFIN setup token stored (waiting to be claimed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetupTokenEditor))]
    public partial bool HasSetupToken { get; private set; }

    /// <summary>Whether both Plaid keys are stored.</summary>
    public bool PlaidKeysComplete => HasClientId && HasSecret;

    /// <summary>Replace was clicked for the client id.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowClientIdEditor))]
    public partial bool IsReplacingClientId { get; set; }

    /// <summary>Replace was clicked for the secret.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSecretEditor))]
    public partial bool IsReplacingSecret { get; set; }

    /// <summary>Replace was clicked for the setup token.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetupTokenEditor))]
    public partial bool IsReplacingSetupToken { get; set; }

    /// <summary>Client id entry shown (not set yet, or replacing).</summary>
    public bool ShowClientIdEditor => !HasClientId || IsReplacingClientId;

    /// <summary>Secret entry shown.</summary>
    public bool ShowSecretEditor => !HasSecret || IsReplacingSecret;

    /// <summary>Setup token entry shown.</summary>
    public bool ShowSetupTokenEditor => !HasSetupToken || IsReplacingSetupToken;

    /// <summary>Typed client id (masked box); cleared after saving.</summary>
    [ObservableProperty]
    public partial string? ClientIdInput { get; set; }

    /// <summary>Typed secret (masked box); cleared after saving.</summary>
    [ObservableProperty]
    public partial string? SecretInput { get; set; }

    /// <summary>Typed SimpleFIN setup token (masked box); cleared after saving.</summary>
    [ObservableProperty]
    public partial string? SetupTokenInput { get; set; }

    /// <summary>Plaid environments.</summary>
    public IReadOnlyList<Choice<PlaidEnvironment>> Environments { get; }

    /// <summary>Environment new links use.</summary>
    [ObservableProperty]
    public partial Choice<PlaidEnvironment> SelectedEnvironment { get; set; }

    /// <summary>Scheduled sync intervals.</summary>
    public IReadOnlyList<Choice<int>> Intervals { get; }

    /// <summary>Selected interval.</summary>
    [ObservableProperty]
    public partial Choice<int> SelectedInterval { get; set; }

    /// <summary>Sync when the app opens the file.</summary>
    [ObservableProperty]
    public partial bool SyncOnStart { get; set; } = true;

    /// <summary>Reloads connections, credentials presence, store, and schedule.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        _loadingSettings = true;
        try
        {
            await Coordinator.RefreshAsync();
            await ReloadCredentialsAsync();
            var settings = await _sync.GetSettingsAsync(CancellationToken.None);
            SelectedInterval = Intervals.FirstOrDefault(i => i.Value == settings.IntervalHours) ?? SelectedInterval;
            SyncOnStart = settings.SyncOnStart;
        }
        catch (SecretStoreException ex)
        {
            ErrorMessage = LedgerText.Format(Strings.Connections_SecretStoreError, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = LedgerText.Format(Strings.Connections_LoadError, ex.Message);
        }
        finally
        {
            _loadingSettings = false;
            IsLoading = false;
            RebuildConnections();
        }
    }

    [RelayCommand]
    private Task AddConnectionAsync() => Coordinator.AddConnectionAsync();

    [RelayCommand]
    private Task SaveClientIdAsync() => StoreAsync(ClientIdInput, _credentials.SetPlaidClientIdAsync, () =>
    {
        ClientIdInput = null;
        IsReplacingClientId = false;
    });

    [RelayCommand]
    private Task SaveSecretAsync() => StoreAsync(SecretInput, _credentials.SetPlaidSecretAsync, () =>
    {
        SecretInput = null;
        IsReplacingSecret = false;
    });

    [RelayCommand]
    private Task SaveSetupTokenAsync() => StoreAsync(SetupTokenInput, _credentials.SetSimpleFinSetupTokenAsync, () =>
    {
        SetupTokenInput = null;
        IsReplacingSetupToken = false;
    });

    [RelayCommand]
    private void ReplaceClientId() => IsReplacingClientId = true;

    [RelayCommand]
    private void ReplaceSecret() => IsReplacingSecret = true;

    [RelayCommand]
    private void ReplaceSetupToken() => IsReplacingSetupToken = true;

    [RelayCommand]
    private void CancelReplace()
    {
        IsReplacingClientId = false;
        IsReplacingSecret = false;
        IsReplacingSetupToken = false;
        ClientIdInput = null;
        SecretInput = null;
        SetupTokenInput = null;
    }

    [RelayCommand]
    private async Task RemovePlaidKeysAsync()
    {
        try
        {
            await _credentials.SetPlaidClientIdAsync(null, CancellationToken.None);
            await _credentials.SetPlaidSecretAsync(null, CancellationToken.None);
            await ReloadCredentialsAsync();
            _status.Show(Strings.Connections_KeysRemoved);
        }
        catch (SecretStoreException ex)
        {
            ErrorMessage = LedgerText.Format(Strings.Connections_SecretStoreError, ex.Message);
        }
    }

    partial void OnSelectedEnvironmentChanged(Choice<PlaidEnvironment> value)
    {
        if (!_loadingSettings)
        {
            _ = SaveEnvironmentAsync(value.Value);
        }
    }

    partial void OnSelectedIntervalChanged(Choice<int> value)
    {
        if (!_loadingSettings)
        {
            _ = SaveSettingsAsync();
        }
    }

    partial void OnSyncOnStartChanged(bool value)
    {
        if (!_loadingSettings)
        {
            _ = SaveSettingsAsync();
        }
    }

    private async Task SaveEnvironmentAsync(PlaidEnvironment environment)
    {
        try
        {
            await _credentials.SetPlaidEnvironmentAsync(environment, CancellationToken.None);
        }
        catch (SecretStoreException ex)
        {
            ErrorMessage = LedgerText.Format(Strings.Connections_SecretStoreError, ex.Message);
        }
    }

    private async Task SaveSettingsAsync()
    {
        var settings = new SyncSettings(SelectedInterval.Value, SyncOnStart);
        try
        {
            await _sync.SaveSettingsAsync(settings, CancellationToken.None);
            Coordinator.ApplySchedule(settings);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = LedgerText.Format(Strings.Connections_LoadError, ex.Message);
        }
    }

    private async Task StoreAsync(string? value, Func<string?, CancellationToken, Task> save, Action done)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ErrorMessage = Strings.Connections_ErrorEmptyValue;
            return;
        }

        ErrorMessage = null;
        try
        {
            await save(value, CancellationToken.None);
            done();
            await ReloadCredentialsAsync();
            _status.Show(Strings.Connections_Saved);
        }
        catch (SecretStoreException ex)
        {
            ErrorMessage = LedgerText.Format(Strings.Connections_SecretStoreError, ex.Message);
        }
    }

    private async Task ReloadCredentialsAsync()
    {
        var store = await _credentials.DescribeStoreAsync(CancellationToken.None);
        StoreText = SyncText.Backend(store.Backend);
        IsWeakerFallback = store.IsWeakerFallback;
        var plaid = await _credentials.GetPlaidStatusAsync(CancellationToken.None);
        HasClientId = plaid.HasClientId;
        HasSecret = plaid.HasSecret;
        var wasLoading = _loadingSettings;
        _loadingSettings = true;
        SelectedEnvironment = Environments.First(e => e.Value == plaid.Environment);
        _loadingSettings = wasLoading;
        HasSetupToken = await _credentials.HasSimpleFinSetupTokenAsync(CancellationToken.None);
    }

    private void RebuildConnections()
    {
        Connections.Clear();
        foreach (var connection in Coordinator.Connections)
        {
            Connections.Add(new ConnectionItemViewModel(connection, Coordinator));
        }

        HasConnections = Connections.Count > 0;
    }
}

/// <summary>One connection in Settings → Connections.</summary>
public sealed partial class ConnectionItemViewModel(SyncConnectionDto connection, SyncCoordinator coordinator) : ObservableObject
{
    /// <summary>The connection.</summary>
    public SyncConnectionDto Connection { get; } = connection;

    /// <summary>Institution.</summary>
    public string InstitutionName => Connection.InstitutionName;

    /// <summary>"Plaid" or "SimpleFIN Bridge".</summary>
    public string ProviderText => SyncText.Provider(Connection.Provider);

    /// <summary>Linked account names.</summary>
    public string AccountsText => Connection.Accounts.Count == 0
        ? Strings.Connections_NoLinkedAccounts
        : string.Join(", ", Connection.Accounts.Select(a => a.Name));

    /// <summary>"Synced 5 min ago".</summary>
    public string LastSyncText => SyncText.LastSync(Connection.LastSyncAt, TimeProvider.System.GetUtcNow());

    /// <summary>"Connected", "Sign-in needed", …</summary>
    public string StatusText => SyncText.Status(Connection.Status);

    /// <summary>Health dot.</summary>
    public HealthKind Health => SyncText.Health(Connection.Status);

    /// <summary>Whether the connection needs a new sign-in (shows Reconnect).</summary>
    public bool NeedsReconnect => Connection.Status == SyncStatus.NeedsReauth;

    /// <summary>What went wrong last time, if anything.</summary>
    public string? ErrorText => Connection.LastError is { } code && Connection.Status != SyncStatus.Ok ? SyncText.ErrorSentence(code) : null;

    /// <summary>Whether the last sync failed (red), as opposed to waiting for the user (amber).</summary>
    public bool IsFailed => Connection.Status == SyncStatus.Error;

    /// <summary>Whether <see cref="ErrorText"/> is set.</summary>
    public bool HasErrorText => ErrorText is not null;

    /// <summary>Screen-reader summary of the row.</summary>
    public string AutomationName => $"{InstitutionName}, {StatusText}, {LastSyncText}";

    [RelayCommand]
    private Task ReconnectAsync() => coordinator.ReconnectAsync(Connection.Id);

    [RelayCommand]
    private Task SyncNowAsync() => coordinator.SyncConnectionAsync(Connection.Id);

    [RelayCommand]
    private Task UnlinkAsync() => coordinator.UnlinkAsync(Connection);
}
