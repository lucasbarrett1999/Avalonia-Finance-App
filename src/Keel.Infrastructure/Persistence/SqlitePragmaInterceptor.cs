using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Keel.Infrastructure.Persistence;

/// <summary>
/// Applies SQLite pragmas every time a connection opens: WAL journaling (crash safety and
/// concurrent readers), foreign keys on, NORMAL sync (safe with WAL), and a busy timeout.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    /// <summary>The pragmas applied on open.</summary>
    public const string Pragmas =
        "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";

    /// <summary>Shared instance (the interceptor is stateless).</summary>
    public static SqlitePragmaInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = Pragmas;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
