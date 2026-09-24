namespace Keel.Application.Security;

/// <summary>Which secret store backs <see cref="ISecretStore"/> on this machine (PRD 6.7).</summary>
public enum SecretStoreBackend
{
    /// <summary>Windows DPAPI (current user) blobs under the data directory.</summary>
    WindowsDpapi,

    /// <summary>The macOS login Keychain.</summary>
    MacOSKeychain,

    /// <summary>The freedesktop Secret Service (GNOME Keyring, KWallet, KeePassXC) over D-Bus.</summary>
    LinuxSecretService,

    /// <summary>The Linux fallback: an AES-GCM encrypted file keyed from the machine id and user name.</summary>
    EncryptedFile,

    /// <summary>Process memory only (tests); nothing survives a restart.</summary>
    InMemory,
}

/// <summary>The selected backend and whether it is the weaker fallback that Settings warns about.</summary>
/// <param name="Backend">Backend in use.</param>
/// <param name="IsWeakerFallback">True for the Linux encrypted-file fallback (PRD 6.7): anyone who can read
/// the user's files and knows the machine id can decrypt it.</param>
public sealed record SecretStoreDescription(SecretStoreBackend Backend, bool IsWeakerFallback);

/// <summary>Describes the secret store chosen at startup (for Settings → Connections).</summary>
public interface ISecretStoreInfo
{
    /// <summary>Selects the store if needed and describes it.</summary>
    Task<SecretStoreDescription> DescribeAsync(CancellationToken ct);
}

/// <summary>A secret store operation failed (store locked, D-Bus error, unreadable blob). The message never contains the secret.</summary>
public sealed class SecretStoreException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SecretStoreException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SecretStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
