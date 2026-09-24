using Keel.Application.Files;
using Keel.Application.Security;
using Keel.Desktop.Services;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Keel.Desktop.Tests;

/// <summary>The app's real host and DI graph over a temporary data directory.</summary>
public sealed class TestHost : IDisposable
{
    private TestHost(string root, Action<IServiceCollection>? configure = null)
    {
        Root = root;
        DataDirectory = new DataDirectory(root);

        // Tests never touch the user's real keyring: secrets stay in memory.
        Host = Program.CreateHost([], DataDirectory, logger: null, services =>
        {
            services.RemoveAll<ISecretStore>();
            services.RemoveAll<ISecretStoreInfo>();
            services.AddSingleton<InMemorySecretStore>();
            services.AddSingleton<ISecretStore>(sp => sp.GetRequiredService<InMemorySecretStore>());
            services.AddSingleton<ISecretStoreInfo>(sp => sp.GetRequiredService<InMemorySecretStore>());
            configure?.Invoke(services);
        });
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

    /// <summary>A host with extra registrations (e.g. a fake bank provider).</summary>
    public static TestHost Create(Action<IServiceCollection> configure) =>
        new(Path.Combine(Path.GetTempPath(), "keel-desktop-tests", Guid.NewGuid().ToString("N")), configure);

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
