using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Files;
using Keel.Application.Security;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Dialogs;

/// <summary>
/// Asks for the passphrase of an encrypted budget file (F-SET-4) and opens it; a wrong passphrase keeps the
/// dialog open with an error. The passphrase is cleared when the dialog closes.
/// </summary>
public sealed partial class UnlockFileViewModel : DialogViewModel
{
    private readonly Func<string, bool, Task> _open;

    /// <summary>Creates the prompt for <paramref name="path"/>; <paramref name="open"/> opens the file with (passphrase, remember).</summary>
    public UnlockFileViewModel(string path, Func<string, bool, Task> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(open);
        Path = path;
        _open = open;
    }

    /// <inheritdoc />
    public override string Title => Strings.Encrypt_UnlockTitle;

    /// <summary>The encrypted file.</summary>
    public string Path { get; }

    /// <summary>"Home.keel is encrypted…".</summary>
    public string Message => LedgerText.Format(Strings.Encrypt_UnlockMessage, System.IO.Path.GetFileName(Path));

    /// <summary>The passphrase typed.</summary>
    [ObservableProperty]
    public partial string? Passphrase { get; set; }

    /// <summary>Keep the key in the OS secret store.</summary>
    [ObservableProperty]
    public partial bool Remember { get; set; }

    /// <summary>Whether "remember" is offered (not for a backup being restored).</summary>
    public bool CanRemember { get; init; } = true;

    /// <summary>Show the passphrase in clear text.</summary>
    [ObservableProperty]
    public partial bool ShowPassphrase { get; set; }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        if (string.IsNullOrEmpty(Passphrase))
        {
            Error = Strings.Encrypt_PassphraseRequired;
            return false;
        }

        try
        {
            await _open(Passphrase, Remember && CanRemember);
            Passphrase = null;
            return true;
        }
        catch (BudgetFileLockedException ex) when (ex.WrongPassphrase)
        {
            Error = Strings.Encrypt_WrongPassphrase;
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = LedgerText.Format(Strings.Encrypt_UnlockFailed, ex.Message);
            return false;
        }
    }

    partial void OnPassphraseChanged(string? value) => Error = null;
}

/// <summary>
/// "Encrypt this file…" (F-SET-4): the new passphrase twice, the plain warning that a lost passphrase means lost
/// data, and the opt-in to remember the key in the OS secret store.
/// </summary>
public sealed partial class EncryptFileViewModel : DialogViewModel
{
    /// <summary>Shortest passphrase accepted.</summary>
    public const int MinimumLength = 8;

    private readonly Func<string, bool, Task> _encrypt;

    /// <summary>Creates the dialog; <paramref name="encrypt"/> converts the file with (passphrase, remember).</summary>
    public EncryptFileViewModel(string fileName, SecretStoreDescription? store, Func<string, bool, Task> encrypt)
    {
        ArgumentNullException.ThrowIfNull(encrypt);
        FileName = fileName;
        _encrypt = encrypt;
        IsWeakerStore = store?.IsWeakerFallback ?? false;
        StoreText = store is null ? null : LedgerText.Format(Strings.Encrypt_SecretStoreIn, EncryptionText.Backend(store.Backend));
    }

    /// <inheritdoc />
    public override string Title => Strings.Encrypt_EncryptTitle;

    /// <summary>The open file's name.</summary>
    public string FileName { get; }

    /// <summary>What happens, with the file name.</summary>
    public string Intro => LedgerText.Format(Strings.Encrypt_EncryptIntro, FileName);

    /// <summary>Where a remembered key goes, or null when unknown.</summary>
    public string? StoreText { get; }

    /// <summary>The store is the weaker Linux fallback file (PRD 6.7).</summary>
    public bool IsWeakerStore { get; }

    /// <summary>New passphrase.</summary>
    [ObservableProperty]
    public partial string? Passphrase { get; set; }

    /// <summary>The same again.</summary>
    [ObservableProperty]
    public partial string? ConfirmPassphrase { get; set; }

    /// <summary>Remember the key on this computer.</summary>
    [ObservableProperty]
    public partial bool Remember { get; set; }

    /// <summary>The user confirmed that a lost passphrase cannot be recovered.</summary>
    [ObservableProperty]
    public partial bool Acknowledged { get; set; }

    /// <summary>Show the passphrases in clear text.</summary>
    [ObservableProperty]
    public partial bool ShowPassphrase { get; set; }

    /// <summary>Checks the entries; returns the error text or null.</summary>
    public string? Validate() =>
        (Passphrase?.Length ?? 0) < MinimumLength ? Strings.Encrypt_TooShort
        : !string.Equals(Passphrase, ConfirmPassphrase, StringComparison.Ordinal) ? Strings.Encrypt_Mismatch
        : !Acknowledged ? Strings.Encrypt_AcknowledgeRequired
        : null;

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        if (Validate() is { } error)
        {
            Error = error;
            return false;
        }

        var passphrase = Passphrase!;
        Passphrase = null;
        ConfirmPassphrase = null;
        await _encrypt(passphrase, Remember);
        return true;
    }
}

/// <summary>"Remove encryption…" (F-SET-4): the current passphrase, checked before the file is converted.</summary>
public sealed partial class RemoveEncryptionViewModel : DialogViewModel
{
    private readonly Func<string, Task<bool>> _verify;
    private readonly Func<string, Task> _remove;

    /// <summary>Creates the dialog.</summary>
    public RemoveEncryptionViewModel(string fileName, Func<string, Task<bool>> verify, Func<string, Task> remove)
    {
        ArgumentNullException.ThrowIfNull(verify);
        ArgumentNullException.ThrowIfNull(remove);
        FileName = fileName;
        _verify = verify;
        _remove = remove;
    }

    /// <inheritdoc />
    public override string Title => Strings.Encrypt_RemoveTitle;

    /// <summary>The open file's name.</summary>
    public string FileName { get; }

    /// <summary>What happens, with the file name.</summary>
    public string Intro => LedgerText.Format(Strings.Encrypt_RemoveIntro, FileName);

    /// <summary>Current passphrase.</summary>
    [ObservableProperty]
    public partial string? Passphrase { get; set; }

    /// <summary>Show the passphrase in clear text.</summary>
    [ObservableProperty]
    public partial bool ShowPassphrase { get; set; }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        if (string.IsNullOrEmpty(Passphrase))
        {
            Error = Strings.Encrypt_PassphraseRequired;
            return false;
        }

        if (!await _verify(Passphrase))
        {
            Error = Strings.Encrypt_WrongPassphrase;
            return false;
        }

        var passphrase = Passphrase;
        Passphrase = null;
        await _remove(passphrase);
        return true;
    }

    partial void OnPassphraseChanged(string? value)
    {
        if (value is { Length: > 0 })
        {
            Error = null;
        }
    }
}

/// <summary>Display text for the encryption UI.</summary>
public static class EncryptionText
{
    /// <summary>Where a remembered key is kept, in words.</summary>
    public static string Backend(SecretStoreBackend backend) =>
        Strings.ResourceManager.GetString("Encrypt_Backend_" + backend, Strings.Culture) ?? backend.ToString();
}
