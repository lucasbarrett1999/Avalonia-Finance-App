using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Files;
using Keel.Application.Security;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.ViewModels.Settings;

/// <summary>
/// Settings → General → Encryption (F-SET-4, ADR 0101): whether the open file is encrypted, "Encrypt this file…",
/// "Remove encryption…", the opt-in to remember the key in the OS secret store, and "Unlock" while the file is
/// locked. Converting the file starts a new session (ADR 0080).
/// </summary>
public sealed partial class EncryptionSettingsViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly IBudgetFileEncryption _encryption;
    private readonly BudgetSessions _sessions;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private SecretStoreDescription? _store;
    private bool _loadingRemember;

    /// <summary>Creates the section.</summary>
    public EncryptionSettingsViewModel(AppSession session, IBudgetFileEncryption encryption, BudgetSessions sessions, DialogService dialogs, StatusService status)
    {
        _session = session;
        _encryption = encryption;
        _sessions = sessions;
        _dialogs = dialogs;
        _status = status;
        IsEncrypted = session.BudgetFile?.IsEncrypted ?? false;
    }

    /// <summary>A budget file is open.</summary>
    public bool HasFile => _session.BudgetFile is not null;

    /// <summary>The encrypted file waits for its passphrase.</summary>
    public bool IsLocked => _session.LockedFile is not null;

    /// <summary>The open file is encrypted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanEncrypt), nameof(CanRemove))]
    public partial bool IsEncrypted { get; private set; }

    /// <summary>A plain file is open.</summary>
    public bool CanEncrypt => HasFile && !IsEncrypted;

    /// <summary>An encrypted file is open.</summary>
    public bool CanRemove => HasFile && IsEncrypted;

    /// <summary>The key is remembered in the OS secret store (two-way: changing it stores or deletes the key).</summary>
    [ObservableProperty]
    public partial bool IsKeyRemembered { get; set; }

    /// <summary>Where remembered keys go.</summary>
    [ObservableProperty]
    public partial string? StoreText { get; private set; }

    /// <summary>The store is the weaker Linux fallback (PRD 6.7).</summary>
    [ObservableProperty]
    public partial bool IsWeakerStore { get; private set; }

    /// <summary>One line describing the file's state.</summary>
    public string StatusText => IsLocked ? Strings.Encrypt_StatusLocked : IsEncrypted ? Strings.Encrypt_StatusEncrypted : Strings.Encrypt_StatusPlain;

    /// <summary>The latest refresh (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the file's encryption state and the secret store.</summary>
    public Task LoadAsync() => Loading = LoadCoreAsync();

    /// <summary>Encrypts the open file after the dialog (passphrase twice, warning, remember).</summary>
    [RelayCommand]
    public async Task EncryptAsync()
    {
        if (_session.BudgetFile is not { IsEncrypted: false } file)
        {
            return;
        }

        var dialog = new EncryptFileViewModel(file.FileName, _store, (passphrase, remember) =>
            _sessions.ChangeEncryptionAsync(file.Path, new BudgetFileEncryptionChange(null, passphrase, remember), Escape(LedgerText.Format(Strings.Encrypt_EncryptedStatus, file.FileName))));
        await _dialogs.ShowAsync(dialog);
    }

    /// <summary>Removes the encryption after the current passphrase is confirmed.</summary>
    [RelayCommand]
    public async Task RemoveEncryptionAsync()
    {
        if (_session.BudgetFile is not { IsEncrypted: true } file)
        {
            return;
        }

        var dialog = new RemoveEncryptionViewModel(
            file.FileName,
            passphrase => _encryption.VerifyPassphraseAsync(passphrase, CancellationToken.None),
            passphrase => _sessions.ChangeEncryptionAsync(file.Path, new BudgetFileEncryptionChange(passphrase, null), Escape(LedgerText.Format(Strings.Encrypt_RemovedStatus, file.FileName))));
        await _dialogs.ShowAsync(dialog);
    }

    /// <summary>Asks for the passphrase of the locked file.</summary>
    [RelayCommand]
    public async Task UnlockAsync()
    {
        if (_session.LockedFile is { } path && !await _sessions.PromptUnlockAsync(_dialogs, path))
        {
            _status.Show(LedgerText.Format(Strings.Encrypt_LockedCancelled, Path.GetFileName(path)));
        }
    }

    partial void OnIsKeyRememberedChanged(bool value)
    {
        if (!_loadingRemember)
        {
            _ = SetRememberedAsync(value);
        }
    }

    private async Task SetRememberedAsync(bool remember)
    {
        try
        {
            await Task.Run(() => _encryption.SetKeyRememberedAsync(remember, CancellationToken.None));
            _status.Show(remember ? Strings.Encrypt_RememberedStatus : Strings.Encrypt_ForgottenStatus);
        }
        catch (Exception ex) when (ex is SecretStoreException or InvalidOperationException)
        {
            _status.Show(LedgerText.Format(Strings.Encrypt_RememberFailed, ex.Message), isError: true);
            await LoadAsync();
        }
    }

    private async Task LoadCoreAsync()
    {
        var status = await Task.Run(() => _encryption.GetStatusAsync(CancellationToken.None));
        _store = status.SecretStore;
        IsEncrypted = status.IsEncrypted;
        _loadingRemember = true;
        try
        {
            IsKeyRemembered = status.IsKeyRemembered;
        }
        finally
        {
            _loadingRemember = false;
        }

        StoreText = _store is null ? null : LedgerText.Format(Strings.Encrypt_SecretStoreIn, EncryptionText.Backend(_store.Backend));
        IsWeakerStore = _store?.IsWeakerFallback ?? false;
    }

    // Startup messages are format strings (the file name is {0}); literal braces must be doubled.
    private static string Escape(string message) => message.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
}
