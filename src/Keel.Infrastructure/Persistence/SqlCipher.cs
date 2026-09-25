using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Keel.Infrastructure.Persistence;

/// <summary>
/// SQLCipher helpers (F-SET-4, ADR 0101). An encrypted budget file is a standard SQLCipher 4 database: its
/// first 16 bytes are a random salt, and its key is PBKDF2-HMAC-SHA512 (256,000 iterations) of the passphrase
/// with that salt, exactly as SQLCipher derives it, so the file also opens with the passphrase in other
/// SQLCipher tools. Keel derives the key once and hands SQLCipher the raw key with the salt
/// (<c>x'KEY SALT'</c>), which skips the per-connection derivation (about 0.3–0.5 s) and makes every copy
/// (backups, moves) keep the salt. Keys and passphrases are never logged.
/// </summary>
public static class SqlCipher
{
    /// <summary>Length of the salt at the start of an encrypted file.</summary>
    public const int SaltLength = 16;

    /// <summary>Key length (AES-256).</summary>
    public const int KeyLength = 32;

    /// <summary>Smallest SQLite page size; database files are a whole number of pages.</summary>
    public const int MinimumPageSize = 512;

    /// <summary>SQLCipher 4's default PBKDF2 iteration count.</summary>
    public const int KdfIterations = 256_000;

    private static ReadOnlySpan<byte> PlainHeader => "SQLite format 3\0"u8;

    /// <summary>
    /// Whether <paramref name="path"/> looks like an SQLCipher database: not the plain SQLite header, and a whole
    /// number of pages long (page sizes are powers of two from 512 bytes). Anything else (a text file, a truncated
    /// file) is left for SQLite to reject as "not a database" rather than asking for a passphrase.
    /// </summary>
    public static bool IsEncrypted(string path) => FileId(path) is not null;

    /// <summary>
    /// The file id of an encrypted file: its salt in lower-case hex (not secret, readable without the key);
    /// null for a plain, missing or non-database file.
    /// </summary>
    public static string? FileId(string path) =>
        ReadHeader(path) is { } header && !header.AsSpan().SequenceEqual(PlainHeader) ? Convert.ToHexStringLower(header) : null;

    /// <summary>The raw key of the file at <paramref name="path"/> for <paramref name="passphrase"/> (derived with the file's salt).</summary>
    /// <exception cref="InvalidOperationException">The file is not encrypted.</exception>
    public static string KeyForFile(string path, string passphrase)
    {
        var header = ReadHeader(path);
        if (header is null || header.AsSpan().SequenceEqual(PlainHeader))
        {
            throw new InvalidOperationException("The budget file is not encrypted.");
        }

        return DeriveKey(passphrase, header);
    }

    /// <summary>A key for a new encrypted file: a fresh random salt and the passphrase derived with it.</summary>
    public static string NewKey(string passphrase) => DeriveKey(passphrase, RandomNumberGenerator.GetBytes(SaltLength));

    /// <summary>The SQLCipher raw key literal <c>x'…'</c>: 32 key bytes then the 16-byte salt, in hex.</summary>
    public static string DeriveKey(string passphrase, ReadOnlySpan<byte> salt)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        if (salt.Length != SaltLength)
        {
            throw new ArgumentException("The salt must be 16 bytes.", nameof(salt));
        }

        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, KdfIterations, HashAlgorithmName.SHA512, KeyLength);
        try
        {
            return string.Create(CultureInfo.InvariantCulture, $"x'{Convert.ToHexString(key)}{Convert.ToHexString(salt)}'");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>The file id (salt in hex) carried by a raw key.</summary>
    public static string FileIdOfKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return key.Length == 2 + (2 * (KeyLength + SaltLength)) + 1
            ? key.Substring(2 + (2 * KeyLength), 2 * SaltLength).ToLowerInvariant()
            : throw new ArgumentException("Not a Keel SQLCipher key.", nameof(key));
    }

    /// <summary>Whether <paramref name="key"/> (null for a plain file) opens the database at <paramref name="path"/>.</summary>
    public static bool CanOpen(string path, string? key)
    {
        try
        {
            using var connection = new SqliteConnection(ConnectionString(path, key, SqliteOpenMode.ReadOnly));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            command.ExecuteScalar();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26)
        {
            // SQLITE_NOTADB: wrong key, missing key, or not a database at all.
            return false;
        }
    }

    /// <summary>
    /// Writes a copy of <paramref name="sourcePath"/> (opened with <paramref name="sourceKey"/>) to the new file
    /// <paramref name="destinationPath"/> encrypted with <paramref name="destinationKey"/> (null writes a plain
    /// file) using <c>sqlcipher_export</c>: schema, triggers, indexes and rows. The source is read inside one
    /// transaction, so the copy is consistent.
    /// </summary>
    public static void Export(string sourcePath, string? sourceKey, string destinationPath, string? destinationKey)
    {
        if (File.Exists(destinationPath))
        {
            throw new IOException("The destination of the encrypted copy already exists.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The budget file does not exist.", Path.GetFileName(sourcePath));
        }

        // ReadWriteCreate: ATTACH uses the main connection's open flags to create the new file.
        using (var connection = new SqliteConnection(ConnectionString(sourcePath, sourceKey, SqliteOpenMode.ReadWriteCreate)))
        {
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout = 5000;");
            using (var attach = connection.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $path AS keel_export KEY $key;";
                attach.Parameters.AddWithValue("$path", destinationPath);
                attach.Parameters.AddWithValue("$key", destinationKey ?? string.Empty);
                attach.ExecuteNonQuery();
            }

            try
            {
                Execute(connection, "BEGIN;");
                using (var export = connection.CreateCommand())
                {
                    export.CommandText = "SELECT sqlcipher_export('keel_export');";
                    export.ExecuteScalar();
                }

                using (var version = connection.CreateCommand())
                {
                    // sqlcipher_export copies schema and rows, not the header's user_version.
                    version.CommandText = "PRAGMA user_version;";
                    var value = Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture);
                    Execute(connection, string.Create(CultureInfo.InvariantCulture, $"PRAGMA keel_export.user_version = {value};"));
                }

                Execute(connection, "COMMIT;");
            }
            finally
            {
                Execute(connection, "DETACH DATABASE keel_export;");
            }
        }

        foreach (var sidecar in new[] { destinationPath + "-wal", destinationPath + "-shm", destinationPath + "-journal" })
        {
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }
    }

    /// <summary>An unpooled connection string for helper connections, keyed when <paramref name="key"/> is set.</summary>
    public static string ConnectionString(string path, string? key, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Pooling = false,
        Password = key ?? string.Empty,
    }.ToString();

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static byte[]? ReadHeader(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < MinimumPageSize || stream.Length % MinimumPageSize != 0)
        {
            return null; // empty (new), truncated, or not a database file
        }

        var header = new byte[SaltLength];
        stream.ReadExactly(header);
        return header;
    }
}
