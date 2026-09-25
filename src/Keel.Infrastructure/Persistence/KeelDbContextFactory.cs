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
    private string? _key;
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

    /// <summary>
    /// The SQLCipher key of the open file (F-SET-4), or null when it is plain. Infrastructure helpers that open
    /// their own connections (backups, integrity check, copies) use it; it never leaves the process.
    /// </summary>
    internal string? CurrentKey
    {
        get
        {
            lock (_gate)
            {
                return _key;
            }
        }
    }

    /// <summary>Points future contexts at <paramref name="path"/>, opened with <paramref name="key"/> when it is encrypted.</summary>
    public void UseFile(string path, string? key = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        lock (_gate)
        {
            _path = fullPath;
            _key = key;
            _options = KeelDatabase.CreateOptions(fullPath, key);
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

    /// <summary>Creates a context for an explicit path (tools, tests, backup verification), keyed when <paramref name="key"/> is set.</summary>
    public static KeelDbContext CreateForFile(string path, string? key = null) => new(KeelDatabase.CreateOptions(path, key));
}
