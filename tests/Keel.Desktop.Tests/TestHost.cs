using Keel.Application.Files;
using Keel.Desktop.Services;
using Keel.Infrastructure.Files;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Keel.Desktop.Tests;

/// <summary>The app's real host and DI graph over a temporary data directory.</summary>
public sealed class TestHost : IDisposable
{
    private TestHost(string root)
    {
        Root = root;
        DataDirectory = new DataDirectory(root);
        Host = Program.CreateHost([], DataDirectory, logger: null);
        Host.Start();

        // Same first-launch path as Program.Main; run off the UI thread to avoid sync-context deadlocks.
        Task.Run(() => Services.GetRequiredService<BudgetFileStartup>().OpenInitialFileAsync(CancellationToken.None))
            .GetAwaiter().GetResult();
    }

    public string Root { get; }

    public IDataDirectory DataDirectory { get; }

    public IHost Host { get; }

    public IServiceProvider Services => Host.Services;

    public static TestHost Create() =>
        new(Path.Combine(Path.GetTempPath(), "keel-desktop-tests", Guid.NewGuid().ToString("N")));

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public void Dispose()
    {
        Host.StopAsync().GetAwaiter().GetResult();
        Host.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
