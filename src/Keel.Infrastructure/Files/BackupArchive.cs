using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Keel.Application.Backup;
using Keel.Application.Files;
using Keel.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Files;

/// <summary>
/// The backup zip format and the SQLite copy helpers shared by backups, restore, "Change location" and
/// the pre-migration backup. A backup zip holds <c>backup.json</c>, the database as <c>Name.keel</c>
/// (a consistent copy made with the SQLite backup API, in rollback-journal mode so it is one file) and
/// the attachments folder under <c>Name.keel-attachments/</c>.
/// </summary>
public static partial class BackupArchive
{
    /// <summary>Name of the manifest entry.</summary>
    public const string ManifestEntry = "backup.json";

    /// <summary>Current manifest format.</summary>
    public const int FormatVersion = 1;

    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Connection string for helper connections: no pooling, so files are released on dispose.</summary>
    public static string UnpooledConnectionString(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Pooling = false,
    }.ToString();

    /// <summary>File name of a backup: <c>Name-YYYYMMDD-HHMMSS[-kind].zip</c>.</summary>
    public static string FileNameFor(string budgetFile, BackupKind kind, DateTime localTime)
    {
        var stem = Path.GetFileNameWithoutExtension(budgetFile);
        var stamp = localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return $"{stem}-{stamp}{Suffix(kind)}.zip";
    }

    /// <summary>Parses a backup file name of <paramref name="budgetFile"/>; null when it is not one.</summary>
    public static (DateTime LocalTime, BackupKind Kind)? Parse(string budgetFile, string zipFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(budgetFile);
        if (!zipFileName.StartsWith(stem + "-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = NameRegex().Match(zipFileName[(stem.Length + 1)..]);
        if (!match.Success || !DateTime.TryParseExact(match.Groups["stamp"].Value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return null;
        }

        var kind = match.Groups["kind"].Value switch
        {
            "auto" => BackupKind.Automatic,
            "before-migration" => BackupKind.BeforeMigration,
            "before-restore" => BackupKind.BeforeRestore,
            _ => BackupKind.Manual,
        };
        return (time, kind);
    }

    /// <summary>
    /// Copies the database at <paramref name="sourcePath"/> to <paramref name="destinationPath"/> with the
    /// SQLite online backup API (consistent even while other connections write) and switches the copy to
    /// rollback-journal mode so it is a single self-contained file.
    /// </summary>
    public static void CopyDatabase(string sourcePath, string destinationPath)
    {
        using (var source = new SqliteConnection(UnpooledConnectionString(sourcePath, SqliteOpenMode.ReadWrite)))
        using (var destination = new SqliteConnection(UnpooledConnectionString(destinationPath)))
        {
            source.Open();
            destination.Open();
            SetBusyTimeout(source);
            source.BackupDatabase(destination);
            using var command = destination.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = DELETE;";
            command.ExecuteNonQuery();
        }

        foreach (var sidecar in new[] { destinationPath + "-wal", destinationPath + "-shm" })
        {
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }
    }

    /// <summary>Writes a verified-format zip of the budget file and its attachments folder.</summary>
    /// <param name="budgetFile">The open budget file.</param>
    /// <param name="attachmentsDirectory">Its attachments folder (may not exist).</param>
    /// <param name="zipPath">Destination zip (must not exist).</param>
    /// <param name="kind">Why the backup is taken.</param>
    /// <param name="createdAtUtc">Time stamp for the manifest.</param>
    public static void Write(string budgetFile, string attachmentsDirectory, string zipPath, BackupKind kind, DateTime createdAtUtc)
    {
        var fileName = Path.GetFileName(budgetFile);
        var work = Path.Combine(Path.GetDirectoryName(zipPath)!, ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var copy = Path.Combine(work, fileName);
            CopyDatabase(budgetFile, copy);
            var partial = zipPath + ".partial";
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                var manifest = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                using (var stream = manifest.Open())
                {
                    JsonSerializer.Serialize(stream, new BackupManifest(FormatVersion, fileName, kind.ToString(), createdAtUtc, LastMigration(copy)));
                }

                zip.CreateEntryFromFile(copy, fileName, CompressionLevel.Optimal);
                if (Directory.Exists(attachmentsDirectory))
                {
                    var folder = Path.GetFileName(attachmentsDirectory);
                    foreach (var file in Directory.EnumerateFiles(attachmentsDirectory, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(attachmentsDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
                        zip.CreateEntryFromFile(file, folder + "/" + relative, CompressionLevel.Optimal);
                    }
                }
            }

            File.Move(partial, zipPath);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    /// <summary>
    /// Extracts the database of a backup zip into <paramref name="workDirectory"/> and checks it: a Keel
    /// manifest, <c>PRAGMA integrity_check</c> = ok, a known schema, and the ledger tables. Returns the
    /// extracted database path.
    /// </summary>
    /// <exception cref="BackupVerificationException">Any check failed.</exception>
    public static string ExtractAndVerify(string zipPath, string workDirectory)
    {
        Directory.CreateDirectory(workDirectory);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var manifestEntry = zip.GetEntry(ManifestEntry) ?? throw new BackupVerificationException("The file is not a Keel backup.");
            BackupManifest manifest;
            using (var stream = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<BackupManifest>(stream) ?? throw new BackupVerificationException("The backup manifest is empty.");
            }

            if (manifest.Format > FormatVersion)
            {
                throw new BackupVerificationException("The backup was made by a newer version of Keel.");
            }

            var dbEntry = zip.GetEntry(manifest.FileName) ?? throw new BackupVerificationException("The backup has no budget file.");
            var extracted = Path.Combine(workDirectory, Path.GetFileName(manifest.FileName));
            dbEntry.ExtractToFile(extracted, overwrite: true);
            VerifyDatabase(extracted);
            return extracted;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or SqliteException)
        {
            throw new BackupVerificationException("The backup could not be read: " + ex.Message, ex);
        }
    }

    /// <summary>Extracts the attachments folder of a backup into <paramref name="attachmentsDirectory"/> (replacing it).</summary>
    public static void ExtractAttachments(string zipPath, string attachmentsDirectory)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var prefix = Path.GetFileName(attachmentsDirectory) + "/";
        TryDeleteDirectory(attachmentsDirectory);
        var root = Path.GetFullPath(attachmentsDirectory);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0))
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName[prefix.Length..]));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue; // Never write outside the attachments folder ("zip slip").
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>Checks a database file: integrity, known migrations, and the ledger tables.</summary>
    /// <exception cref="BackupVerificationException">A check failed.</exception>
    public static void VerifyDatabase(string path)
    {
        using var connection = new SqliteConnection(UnpooledConnectionString(path, SqliteOpenMode.ReadOnly));
        connection.Open();
        var problems = IntegrityCheck(connection);
        if (problems.Count > 0)
        {
            throw new BackupVerificationException("The copied database failed its integrity check: " + problems[0]);
        }

        var applied = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";""";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                applied.Add(reader.GetString(0));
            }
        }

        using (var db = KeelDbContextFactory.CreateForFile(path))
        {
            var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            if (applied.Count == 0)
            {
                throw new BackupVerificationException("The copied database has no Keel schema.");
            }

            if (applied.Any(m => !known.Contains(m)))
            {
                throw new BackupVerificationException("The backup was made by a newer version of Keel. Update Keel to restore it.");
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """SELECT (SELECT COUNT(*) FROM "Transactions") + (SELECT COUNT(*) FROM "Accounts") + (SELECT COUNT(*) FROM "Categories");""";
            command.ExecuteScalar();
        }
    }

    /// <summary>Waits up to five seconds for other connections' locks, like the app's own connections.</summary>
    public static void SetBusyTimeout(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
    }

    /// <summary>Runs <c>PRAGMA integrity_check</c>; returns SQLite's messages, empty when "ok".</summary>
    public static IReadOnlyList<string> IntegrityCheck(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check(20);";
        var messages = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            messages.Add(reader.GetString(0));
        }

        return messages is ["ok"] ? [] : messages;
    }

    /// <summary>Deletes a directory tree, ignoring failures (temporary folders).</summary>
    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Copies a directory tree (attachments).</summary>
    public static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static string? LastMigration(string path)
    {
        using var connection = new SqliteConnection(UnpooledConnectionString(path, SqliteOpenMode.ReadOnly));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """SELECT MAX("MigrationId") FROM "__EFMigrationsHistory";""";
        return command.ExecuteScalar() as string;
    }

    private static string Suffix(BackupKind kind) => kind switch
    {
        BackupKind.Automatic => "-auto",
        BackupKind.BeforeMigration => "-before-migration",
        BackupKind.BeforeRestore => "-before-restore",
        _ => string.Empty,
    };

    [GeneratedRegex(@"^(?<stamp>\d{8}-\d{6})(?:-(?<kind>auto|before-migration|before-restore))?(?:-\d+)?\.zip$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    /// <summary>The <c>backup.json</c> manifest.</summary>
    /// <param name="Format">Format version.</param>
    /// <param name="FileName">Database entry name.</param>
    /// <param name="Kind">Backup kind.</param>
    /// <param name="CreatedAt">UTC time stamp.</param>
    /// <param name="LastMigration">Newest migration in the copy.</param>
    private sealed record BackupManifest(int Format, string FileName, string Kind, DateTime CreatedAt, string? LastMigration);
}

/// <summary>Paths of the backups folder for a data directory.</summary>
internal static class BackupPaths
{
    public static string UniqueZipPath(IDataDirectory dataDirectory, string budgetFile, BackupKind kind, DateTime localTime)
    {
        Directory.CreateDirectory(dataDirectory.BackupsDirectory);
        var name = BackupArchive.FileNameFor(budgetFile, kind, localTime);
        var path = Path.Combine(dataDirectory.BackupsDirectory, name);
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(dataDirectory.BackupsDirectory, Path.GetFileNameWithoutExtension(name) + "-" + i.ToString(CultureInfo.InvariantCulture) + ".zip");
        }

        return path;
    }
}
