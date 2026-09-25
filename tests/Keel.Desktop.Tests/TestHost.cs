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
    private TestHost(string root, Action<IServiceCollection>? configure = null, bool firstRun = false)
    {
        Root = root;
        DataDirectory = new DataDirectory(root);
        if (!firstRun)
        {
            // Tests start in a budget file, as a returning user does; FirstRunTests opt into the setup.
            Directory.CreateDirectory(root);
            File.WriteAllText(DataDirectory.SettingsFile, """{ "version": 1, "firstRunCompleted": true }""");
        }

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
        Sessions = Host.Services.GetRequiredService<BudgetSessions>();

        // Same first-launch path as Program.Main; run off the UI thread to avoid sync-context deadlocks.
        Task.Run(() => Services.GetRequiredService<BudgetFileStartup>().OpenInitialFileAsync(CancellationToken.None))
            .GetAwaiter().GetResult();
    }

    public string Root { get; }

    public IDataDirectory DataDirectory { get; }

    public IHost Host { get; }

    public IServiceProvider Services => Host.Services;

    /// <summary>The budget-file sessions (shared by every host the test opens).</summary>
    public BudgetSessions Sessions { get; }

    /// <summary>The services of the session running now (differs from <see cref="Services"/> after a file switch).</summary>
    public IServiceProvider CurrentServices => Sessions.Current?.Services ?? Services;

    /// <summary>A service of the session running now.</summary>
    public T Current<T>()
        where T : notnull => CurrentServices.GetRequiredService<T>();

    public static TestHost Create() =>
        new(Path.Combine(Path.GetTempPath(), "keel-desktop-tests", Guid.NewGuid().ToString("N")));

    /// <summary>A host with extra registrations (e.g. a fake bank provider).</summary>
    public static TestHost Create(Action<IServiceCollection> configure) =>
        new(Path.Combine(Path.GetTempPath(), "keel-desktop-tests", Guid.NewGuid().ToString("N")), configure);

    /// <summary>A true first launch: no settings.json, so the first-run setup shows (PRD 9.10).</summary>
    public static TestHost CreateFirstRun(Action<IServiceCollection>? configure = null) =>
        new(Path.Combine(Path.GetTempPath(), "keel-desktop-tests", Guid.NewGuid().ToString("N")), configure, firstRun: true);

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public void Dispose()
    {
        if (Sessions.Current is { } current && !ReferenceEquals(current, Host))
        {
            current.StopAsync().GetAwaiter().GetResult();
            current.Dispose();
        }

        try
        {
            Host.StopAsync().GetAwaiter().GetResult();
            Host.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // A file switch already closed the first session.
        }

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
