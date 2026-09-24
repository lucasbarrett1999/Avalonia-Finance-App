using Microsoft.EntityFrameworkCore.Design;

namespace Keel.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef</c> to create migrations; points at a throwaway file.</summary>
public sealed class DesignTimeKeelDbContextFactory : IDesignTimeDbContextFactory<KeelDbContext>
{
    /// <inheritdoc />
    public KeelDbContext CreateDbContext(string[] args) =>
        KeelDbContextFactory.CreateForFile(Path.Combine(Path.GetTempPath(), "keel-design-time.keel"));
}
