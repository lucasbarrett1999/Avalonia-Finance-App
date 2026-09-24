using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Keel.Application.Security;

namespace Keel.Infrastructure.Platform.Windows;

/// <summary>
/// Windows secret store (PRD 6.7): each secret is a DPAPI blob (<see cref="DataProtectionScope.CurrentUser"/>)
/// in its own file under <c>&lt;datadir&gt;/secrets/</c>. Only the same Windows user on the same
/// machine (or with the same roaming profile) can decrypt it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiSecretStore(string directory) : ISecretStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key)
    {
        var path = Path.Combine(directory, SecretFiles.FileName(key));
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var blob = SecretFiles.ReadOrNull(path);
            if (blob is null)
            {
                return null;
            }

            if (!DpapiBlob.TryUnframe(blob, out var protectedBytes))
            {
                throw new SecretStoreException("A stored secret has an unknown format.");
            }

            try
            {
                var clear = ProtectedData.Unprotect(protectedBytes, DpapiBlob.Entropy(key), DataProtectionScope.CurrentUser);
                try
                {
                    return Encoding.UTF8.GetString(clear);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clear);
                }
            }
            catch (CryptographicException ex)
            {
                throw new SecretStoreException("A stored secret could not be decrypted for this Windows user.", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = Path.Combine(directory, SecretFiles.FileName(key));
        var clear = Encoding.UTF8.GetBytes(value);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, DpapiBlob.Entropy(key), DataProtectionScope.CurrentUser);
            SecretFiles.WriteAtomic(path, DpapiBlob.Frame(protectedBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key)
    {
        var path = Path.Combine(directory, SecretFiles.FileName(key));
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            SecretFiles.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// The platform-neutral part of the DPAPI store: blob framing (<c>KDP1</c> magic, then the DPAPI
/// output) and the optional entropy, which binds each blob to its key name so blobs cannot be
/// swapped between keys.
/// </summary>
internal static class DpapiBlob
{
    /// <summary>Magic and version of the file format.</summary>
    public static ReadOnlySpan<byte> Magic => "KDP1"u8;

    /// <summary>Frames DPAPI output for storage.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> protectedBytes) => SecretBlob.Frame(Magic, protectedBytes);

    /// <summary>The DPAPI output of a stored blob, or false when the blob has another format.</summary>
    public static bool TryUnframe(byte[] blob, out byte[] protectedBytes)
    {
        if (SecretBlob.TryUnframe(Magic, blob, out var payload))
        {
            protectedBytes = payload.ToArray();
            return true;
        }

        protectedBytes = [];
        return false;
    }

    /// <summary>The DPAPI optional entropy for <paramref name="key"/>.</summary>
    public static byte[] Entropy(string key) => Encoding.UTF8.GetBytes("Keel secret v1\n" + key);
}
