using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Persistence;

/// <summary>Builds EF Core options for a budget file.</summary>
public static class KeelDatabase
{
    /// <summary>
    /// Connection string for a <c>.keel</c> file (created if missing, foreign keys on). With a SQLCipher
    /// <paramref name="key"/> (F-SET-4) Microsoft.Data.Sqlite sends <c>PRAGMA key</c> as the first statement of
    /// every connection, before the pragma interceptor runs; EF Core's read-only existence check copies it.
    /// </summary>
    public static string ConnectionString(string path, string? key = null) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true,
        Pooling = true,
        Password = key ?? string.Empty,
    }.ToString();

    /// <summary>Options for a context over the budget file at <paramref name="path"/>, keyed when it is encrypted.</summary>
    public static DbContextOptions<KeelDbContext> CreateOptions(string path, string? key = null) =>
        new DbContextOptionsBuilder<KeelDbContext>()
            .UseSqlite(ConnectionString(path, key), sqlite => sqlite.MigrationsAssembly(typeof(KeelDatabase).Assembly.GetName().Name))
            .AddInterceptors(SqlitePragmaInterceptor.Instance)
            .Options;
}
