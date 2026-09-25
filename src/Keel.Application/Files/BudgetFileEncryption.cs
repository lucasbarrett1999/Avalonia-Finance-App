using Keel.Application.Security;

namespace Keel.Application.Files;

/// <summary>
/// Optional SQLCipher encryption of a budget file (F-SET-4, ADR 0101). A file is either plain SQLite or
/// encrypted with a key derived from the user's passphrase; the passphrase can be remembered in the OS
/// secret store (<see cref="ISecretStore"/>) on the user's request. Session service.
/// </summary>
public interface IBudgetFileEncryption
{
    /// <summary>Whether the open file is encrypted and whether its key is remembered on this computer.</summary>
    Task<BudgetFileEncryptionStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Checks <paramref name="passphrase"/> against the open (encrypted) file without changing anything.</summary>
    Task<bool> VerifyPassphraseAsync(string passphrase, CancellationToken ct);

    /// <summary>Stores the open file's key in the OS secret store, or deletes it from there.</summary>
    /// <exception cref="SecretStoreException">The secret store refused the change.</exception>
    Task SetKeyRememberedAsync(bool remember, CancellationToken ct);

    /// <summary>
    /// Encrypts or decrypts the budget file at <paramref name="path"/>, which no session may have open: takes a
    /// verified backup of the file first (<c>-before-encryption</c> / <c>-before-decryption</c>), writes the
    /// converted copy next to it with SQLCipher's <c>sqlcipher_export</c>, verifies the copy by reopening it
    /// (integrity check and known schema) and only then swaps it in. The original is untouched on failure.
    /// </summary>
    /// <exception cref="BudgetFileLockedException">The current passphrase does not open the file.</exception>
    Task ConvertAsync(string path, BudgetFileEncryptionChange change, CancellationToken ct);
}

/// <summary>Encryption state of the open budget file.</summary>
/// <param name="IsEncrypted">The file is SQLCipher-encrypted.</param>
/// <param name="IsKeyRemembered">Its key is in the OS secret store, so it opens without a prompt.</param>
/// <param name="SecretStore">The secret store the key would go to (the Linux file fallback is weaker, PRD 6.7).</param>
public sealed record BudgetFileEncryptionStatus(bool IsEncrypted, bool IsKeyRemembered, SecretStoreDescription? SecretStore);

/// <summary>A change of a budget file's encryption.</summary>
/// <param name="CurrentPassphrase">Passphrase of the encrypted file; null when it is plain (encrypt).</param>
/// <param name="NewPassphrase">Passphrase to encrypt with; null removes the encryption (decrypt).</param>
/// <param name="RememberKey">Keep the new key in the OS secret store (encrypt only).</param>
public sealed record BudgetFileEncryptionChange(string? CurrentPassphrase, string? NewPassphrase, bool RememberKey = false)
{
    /// <summary>The change encrypts a plain file.</summary>
    public bool Encrypts => CurrentPassphrase is null && NewPassphrase is not null;

    /// <summary>The change removes the encryption.</summary>
    public bool Decrypts => CurrentPassphrase is not null && NewPassphrase is null;

    /// <inheritdoc />
    public override string ToString() => Encrypts ? "Encrypt" : Decrypts ? "Decrypt" : "Change";
}

/// <summary>A passphrase entered to open an encrypted budget file.</summary>
/// <param name="Passphrase">The passphrase.</param>
/// <param name="Remember">Keep the file's key in the OS secret store.</param>
public sealed record BudgetFileUnlock(string Passphrase, bool Remember = false)
{
    /// <inheritdoc />
    public override string ToString() => "BudgetFileUnlock";
}

/// <summary>
/// Keys of encrypted budget files unlocked in this app run, by file id (the file's SQLCipher salt, so a moved
/// copy or a backup of the file has the same id). One instance is shared by every session (ADR 0080) so
/// reopening a file after a restore or a move does not ask again; nothing is written anywhere.
/// </summary>
public sealed class BudgetFileKeyRing
{
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Remembers the key of the file with <paramref name="fileId"/> for this app run.</summary>
    public void Remember(string fileId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            _keys[fileId] = key;
        }
    }

    /// <summary>The key of the file with <paramref name="fileId"/>, or null.</summary>
    public string? Find(string fileId)
    {
        lock (_gate)
        {
            return _keys.GetValueOrDefault(fileId);
        }
    }

    /// <summary>Forgets the key of the file with <paramref name="fileId"/>.</summary>
    public void Forget(string fileId)
    {
        lock (_gate)
        {
            _keys.Remove(fileId);
        }
    }
}

/// <summary>
/// A budget file (or backup) is encrypted and no passphrase, or a wrong one, was given. The message names the
/// file only; it never contains a passphrase or key.
/// </summary>
public sealed class BudgetFileLockedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public BudgetFileLockedException(string path, bool wrongPassphrase)
        : base(wrongPassphrase
            ? $"The passphrase does not open '{System.IO.Path.GetFileName(path)}'."
            : $"'{System.IO.Path.GetFileName(path)}' is encrypted. Enter its passphrase to open it.")
    {
        Path = path;
        WrongPassphrase = wrongPassphrase;
    }

    /// <summary>Creates the exception.</summary>
    public BudgetFileLockedException()
        : this(string.Empty, false)
    {
    }

    /// <summary>Creates the exception.</summary>
    public BudgetFileLockedException(string message)
        : base(message)
    {
        Path = string.Empty;
    }

    /// <summary>Creates the exception.</summary>
    public BudgetFileLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Path = string.Empty;
    }

    /// <summary>The encrypted file.</summary>
    public string Path { get; }

    /// <summary>A passphrase was given but does not open the file.</summary>
    public bool WrongPassphrase { get; }
}
