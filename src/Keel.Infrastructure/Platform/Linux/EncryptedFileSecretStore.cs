using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keel.Application.Security;
using Konscious.Security.Cryptography;

namespace Keel.Infrastructure.Platform.Linux;

/// <summary>Argon2id cost parameters of the fallback key.</summary>
/// <param name="MemoryKiB">Memory in KiB.</param>
/// <param name="Iterations">Passes.</param>
/// <param name="Parallelism">Lanes.</param>
internal sealed record Argon2Parameters(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>OWASP's first recommended Argon2id setting (64 MiB, 3 passes, 1 lane).</summary>
    public static Argon2Parameters Default { get; } = new(64 * 1024, 3, 1);
}

/// <summary>
/// The Linux fallback secret store (PRD 6.7) for machines without a Secret Service: each secret is
/// an AES-256-GCM file under <c>&lt;datadir&gt;/secrets/</c>, keyed by Argon2id over the machine id
/// and user name with a random per-install salt. It keeps secrets out of plain text and ties them
/// to the machine, but anyone who can read the user's files can derive the key, so Settings shows
/// it as the weaker fallback.
/// </summary>
/// <remarks>
/// Files: <c>fallback-key.json</c> (salt and Argon2id parameters, not secret) and one
/// <c>&lt;sha256(key)&gt;.secret</c> per secret: magic <c>KEF1</c>, 12-byte nonce, 16-byte tag,
/// ciphertext; the key name is the associated data, so a file cannot be moved to another key.
/// </remarks>
internal sealed class EncryptedFileSecretStore : ISecretStore
{
    private const string KeyFileName = "fallback-key.json";
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;

    private readonly string _directory;
    private readonly Func<string> _passwordSource;
    private readonly Argon2Parameters _newKeyParameters;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _key;

    /// <summary>Creates the store over <paramref name="directory"/>.</summary>
    /// <param name="directory">The secrets directory.</param>
    /// <param name="passwordSource">Key material; defaults to <see cref="MachineSecret.Read"/>.</param>
    /// <param name="newKeyParameters">Argon2id cost for a new install (tests pass a cheap one); an existing key file keeps its own.</param>
    public EncryptedFileSecretStore(string directory, Func<string>? passwordSource = null, Argon2Parameters? newKeyParameters = null)
    {
        _directory = directory;
        _passwordSource = passwordSource ?? MachineSecret.Read;
        _newKeyParameters = newKeyParameters ?? Argon2Parameters.Default;
    }

    /// <summary>Magic and version of a secret file.</summary>
    public static ReadOnlySpan<byte> Magic => "KEF1"u8;

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var blob = SecretFiles.ReadOrNull(PathOf(key));
            if (blob is null)
            {
                return null;
            }

            var aesKey = await KeyAsync().ConfigureAwait(false);
            return Decrypt(aesKey, key, blob);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var aesKey = await KeyAsync().ConfigureAwait(false);
            SecretFiles.WriteAtomic(PathOf(key), Encrypt(aesKey, key, value));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            SecretFiles.Delete(PathOf(key));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Encrypts <paramref name="value"/> for <paramref name="key"/> (framed file bytes).</summary>
    internal static byte[] Encrypt(byte[] aesKey, string key, string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        var payload = new byte[NonceLength + TagLength + plain.Length];
        var nonce = payload.AsSpan(0, NonceLength);
        var tag = payload.AsSpan(NonceLength, TagLength);
        var cipher = payload.AsSpan(NonceLength + TagLength);
        RandomNumberGenerator.Fill(nonce);
        try
        {
            using var aes = new AesGcm(aesKey, TagLength);
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(key));
            return SecretBlob.Frame(Magic, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Decrypts a framed file for <paramref name="key"/>.</summary>
    internal static string Decrypt(byte[] aesKey, string key, byte[] blob)
    {
        if (!SecretBlob.TryUnframe(Magic, blob, out var payload) || payload.Length < NonceLength + TagLength)
        {
            throw new SecretStoreException("A stored secret has an unknown format.");
        }

        var span = payload.Span;
        var plain = new byte[span.Length - NonceLength - TagLength];
        try
        {
            using var aes = new AesGcm(aesKey, TagLength);
            aes.Decrypt(span[..NonceLength], span[(NonceLength + TagLength)..], span.Slice(NonceLength, TagLength), plain, Encoding.UTF8.GetBytes(key));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new SecretStoreException("A stored secret could not be decrypted on this machine for this user.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Argon2id of <paramref name="password"/>.</summary>
    internal static byte[] DeriveKey(string password, byte[] salt, Argon2Parameters parameters)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = parameters.MemoryKiB,
                Iterations = parameters.Iterations,
                DegreeOfParallelism = parameters.Parallelism,
            };
            return argon.GetBytes(KeyLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private string PathOf(string key) => Path.Combine(_directory, SecretFiles.FileName(key));

    // The AES key, derived once per process (Argon2id is deliberately slow).
    private async Task<byte[]> KeyAsync()
    {
        if (_key is not null)
        {
            return _key;
        }

        var keyFile = Path.Combine(_directory, KeyFileName);
        KeyFile? stored = null;
        if (SecretFiles.ReadOrNull(keyFile) is { } json)
        {
            try
            {
                stored = JsonSerializer.Deserialize<KeyFile>(json);
            }
            catch (JsonException ex)
            {
                throw new SecretStoreException("The secret key file is unreadable.", ex);
            }
        }

        if (stored is null)
        {
            stored = new KeyFile(1, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)), _newKeyParameters.MemoryKiB, _newKeyParameters.Iterations, _newKeyParameters.Parallelism);
            SecretFiles.WriteAtomic(keyFile, JsonSerializer.SerializeToUtf8Bytes(stored));
        }

        var salt = Convert.FromBase64String(stored.Salt);
        var parameters = new Argon2Parameters(stored.MemoryKiB, stored.Iterations, stored.Parallelism);
        var password = _passwordSource();
        _key = await Task.Run(() => DeriveKey(password, salt, parameters)).ConfigureAwait(false);
        return _key;
    }

    private sealed record KeyFile(int Version, string Salt, int MemoryKiB, int Iterations, int Parallelism);
}

/// <summary>The key material of the fallback store: the machine id and the user name.</summary>
internal static class MachineSecret
{
    /// <summary>Files tried for the machine id, in order.</summary>
    public static IReadOnlyList<string> MachineIdFiles { get; } = ["/etc/machine-id", "/var/lib/dbus/machine-id"];

    /// <summary><c>machine-id + "\n" + user name</c>; the host name stands in when no machine id file exists.</summary>
    public static string Read()
    {
        var machineId = MachineIdFiles.Select(ReadTrimmed).FirstOrDefault(id => !string.IsNullOrEmpty(id)) ?? Environment.MachineName;
        return machineId + "\n" + Environment.UserName;
    }

    private static string? ReadTrimmed(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
