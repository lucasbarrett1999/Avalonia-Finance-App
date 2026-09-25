using Keel.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Keel.Infrastructure.Portability;

/// <summary>
/// A deferred read transaction on a context: every query of an export sees one consistent snapshot of the
/// file (SQLite WAL), and writers are not blocked while the export runs.
/// </summary>
internal sealed class ReadSnapshot : IAsyncDisposable
{
    private readonly KeelDbContext _db;
    private readonly SqliteTransaction _transaction;

    private ReadSnapshot(KeelDbContext db, SqliteTransaction transaction)
    {
        _db = db;
        _transaction = transaction;
    }

    /// <summary>Opens the context's connection and starts the snapshot.</summary>
    public static async Task<ReadSnapshot> BeginAsync(KeelDbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var transaction = connection.BeginTransaction(deferred: true);
        await db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);
        return new ReadSnapshot(db, transaction);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _db.Database.UseTransactionAsync(null).ConfigureAwait(false);
        _transaction.Rollback();
        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
}
