using System.Security.Cryptography;
using System.Text;

namespace Keel.Infrastructure.Platform;

/// <summary>
/// File layout shared by the file-backed secret stores (Windows DPAPI blobs and the Linux
/// encrypted-file fallback) under <c>&lt;datadir&gt;/secrets/</c> (PRD 7.4): one file per secret,
/// named by the SHA-256 of its key so key names never become paths, written atomically, readable
/// only by the user on Unix.
/// </summary>
internal static class SecretFiles
{
    /// <summary>Extension of a secret file.</summary>
    public const string Extension = ".secret";

    /// <summary>The file name of the secret <paramref name="key"/>: 64 lower-case hex digits plus <see cref="Extension"/>.</summary>
    public static string FileName(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + Extension;
    }

    /// <summary>Creates the directory (mode 700 on Unix).</summary>
    public static void EnsureDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Reads a file, or null when it does not exist.</summary>
    public static byte[]? ReadOrNull(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Writes a file through a temporary sibling and a rename, so a crash never leaves half a secret.</summary>
    public static void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        EnsureDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Deletes a file; no-op when absent.</summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

/// <summary>A magic-prefixed secret blob: 4 ASCII bytes naming the format and version, then the payload.</summary>
internal static class SecretBlob
{
    /// <summary>Length of the magic prefix.</summary>
    public const int MagicLength = 4;

    /// <summary>Prefixes <paramref name="payload"/> with <paramref name="magic"/>.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> magic, ReadOnlySpan<byte> payload)
    {
        if (magic.Length != MagicLength)
        {
            throw new ArgumentException("The magic must be 4 bytes.", nameof(magic));
        }

        var blob = new byte[MagicLength + payload.Length];
        magic.CopyTo(blob);
        payload.CopyTo(blob.AsSpan(MagicLength));
        return blob;
    }

    /// <summary>The payload after <paramref name="magic"/>, or false when the blob has another format.</summary>
    public static bool TryUnframe(ReadOnlySpan<byte> magic, byte[] blob, out ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < MagicLength || !blob.AsSpan(0, MagicLength).SequenceEqual(magic))
        {
            payload = default;
            return false;
        }

        payload = blob.AsMemory(MagicLength);
        return true;
    }
}
