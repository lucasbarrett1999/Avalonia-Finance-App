using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Persistence;

/// <summary>Builds EF Core options for a budget file.</summary>
public static class KeelDatabase
{
    /// <summary>Connection string for a <c>.keel</c> file (created if missing, foreign keys on).</summary>
    public static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true,
        Pooling = true,
    }.ToString();

    /// <summary>Options for a context over the budget file at <paramref name="path"/>.</summary>
    public static DbContextOptions<KeelDbContext> CreateOptions(string path) =>
        new DbContextOptionsBuilder<KeelDbContext>()
            .UseSqlite(ConnectionString(path), sqlite => sqlite.MigrationsAssembly(typeof(KeelDatabase).Assembly.GetName().Name))
            .AddInterceptors(SqlitePragmaInterceptor.Instance)
            .Options;
}
