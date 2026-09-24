using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Persistence;

/// <summary>
/// Creates short-lived <see cref="KeelDbContext"/> instances for the budget file that is
/// currently open. Services create one context per unit of work.
/// </summary>
public sealed class KeelDbContextFactory : IDbContextFactory<KeelDbContext>
{
    private readonly Lock _gate = new();
    private string? _path;
    private DbContextOptions<KeelDbContext>? _options;

    /// <summary>Full path of the budget file contexts point at, or null when none is open.</summary>
    public string? CurrentPath
    {
        get
        {
            lock (_gate)
            {
                return _path;
            }
        }
    }

    /// <summary>Points future contexts at <paramref name="path"/>.</summary>
    public void UseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        lock (_gate)
        {
            _path = fullPath;
            _options = KeelDatabase.CreateOptions(fullPath);
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No budget file is open.</exception>
    public KeelDbContext CreateDbContext()
    {
        DbContextOptions<KeelDbContext>? options;
        lock (_gate)
        {
            options = _options;
        }

        return new KeelDbContext(options ?? throw new InvalidOperationException("No budget file is open."));
    }

    /// <summary>Creates a context for an explicit path (tools, tests, backup verification).</summary>
    public static KeelDbContext CreateForFile(string path) => new(KeelDatabase.CreateOptions(path));
}
