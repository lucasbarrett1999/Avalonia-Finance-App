using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Keel.Infrastructure.Persistence;

/// <summary>
/// Applies SQLite pragmas every time a connection opens: WAL journaling (crash safety and
/// concurrent readers), foreign keys on, NORMAL sync (safe with WAL), and a busy timeout. Read-only
/// connections (EF Core's existence check opens one) cannot change the journal mode, so they get the
/// other pragmas only; the first read-write connection switches a copied or restored file to WAL. Connections
/// to an encrypted file (a key in the connection string) also get <see cref="EncryptedCachePragma"/>.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    /// <summary>The pragmas applied on open.</summary>
    public const string Pragmas =
        "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";

    /// <summary>The pragmas applied to a read-only connection.</summary>
    public const string ReadOnlyPragmas = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";

    /// <summary>
    /// Extra pragma for connections to an SQLCipher-encrypted file (F-SET-4, ADR 0101): a page cache of up to
    /// 64 MiB. Decrypting a page costs far more than copying it from the OS cache, so with SQLite's default
    /// 2 MiB cache the 100k-row register took 4–6 times as long; the cache fills only with pages actually read.
    /// Microsoft.Data.Sqlite has already sent <c>PRAGMA key</c> by the time this runs.
    /// </summary>
    public const string EncryptedCachePragma = "PRAGMA cache_size = -65536;";

    /// <summary>Shared instance (the interceptor is stateless).</summary>
    public static SqlitePragmaInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = PragmasFor(connection);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = PragmasFor(connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string PragmasFor(DbConnection connection)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection.ConnectionString);
        var pragmas = builder.Mode == Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly ? ReadOnlyPragmas : Pragmas;
        return string.IsNullOrEmpty(builder.Password) ? pragmas : pragmas + " " + EncryptedCachePragma;
    }
}
