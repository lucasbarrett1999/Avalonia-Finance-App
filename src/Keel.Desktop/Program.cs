using Avalonia;
using Avalonia.Threading;
using Keel.Application.Files;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Infrastructure;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Logging;
using Keel.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Velopack;

namespace Keel.Desktop;

/// <summary>Entry point and composition root. The only place that references Keel.Infrastructure.</summary>
internal static class Program
{
    /// <summary>
    /// Runs Velopack's install hooks, enforces a single instance per data directory (a second launch
    /// hands its <c>.keel</c> path to the first and exits), applies the format culture, starts the first
    /// budget-file session, and runs the Avalonia app.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack install/update/uninstall hooks run and exit here; on Windows they (un)register .keel files.
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            velopack = velopack
                .OnAfterInstallFastCallback(_ => RegisterFileType())
                .OnAfterUpdateFastCallback(_ => RegisterFileType())
                .OnBeforeUninstallFastCallback(_ => UnregisterFileType());
        }

        velopack.Run();

        var dataDirectory = DataDirectory.ForCurrentUser();
        dataDirectory.EnsureCreated();
        using var logger = KeelLogging.CreateLogger(dataDirectory);
        var requestedFile = LaunchArguments.BudgetFile(args);

        using var instance = SingleInstance.TryAcquire(dataDirectory.Root);
        if (instance is null && SingleInstance.TrySend(dataDirectory.Root, requestedFile))
        {
            logger.Information("Keel is already running; handed the launch over");
            return 0;
        }

        try
        {
            logger.Information("Keel starting");
            var settings = new JsonAppSettingsStore(dataDirectory);
            LocaleService.ApplyCulture(settings.Current.FormatCulture);
            using var sessions = CreateSessions(args, dataDirectory, logger, settings);
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                // The Stats page's cold start runs from here to the first interactive frame (PRD 4).
                sessions.ColdStartedAt = process.StartTime.ToUniversalTime();
            }

            var host = sessions.Start(new BudgetStartupOptions(requestedFile));
            App.Services = host.Services;
            instance?.Listen(path => Dispatcher.UIThread.Post(() => _ = sessions.ActivateAsync(path)));

            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

            sessions.Current?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            logger.Information("Keel stopped");
            return exitCode;
        }
        catch (Exception ex)
        {
            logger.Fatal(ex, "Keel terminated unexpectedly");
            return 1;
        }
    }

    /// <summary>Avalonia configuration; also used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void RegisterFileType()
    {
        if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } executable)
        {
            Keel.Infrastructure.Platform.Windows.WindowsFileAssociation.Register(executable);
        }
    }

    private static void UnregisterFileType()
    {
        if (OperatingSystem.IsWindows())
        {
            Keel.Infrastructure.Platform.Windows.WindowsFileAssociation.Unregister();
        }
    }

    /// <summary>
    /// Builds the generic host of one budget-file session with DI for infrastructure and desktop
    /// services, together with the <see cref="BudgetSessions"/> that can replace it (tests use this).
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="dataDirectory">Data directory (tests pass a temporary one).</param>
    /// <param name="logger">Serilog logger; null disables logging (tests).</param>
    public static IHost CreateHost(string[] args, IDataDirectory dataDirectory, Serilog.ILogger? logger) =>
        CreateHost(args, dataDirectory, logger, configure: null);

    /// <summary>
    /// Builds the generic host; <paramref name="configure"/> runs last (tests swap the secret store and bank
    /// providers) and also applies to every later session the returned host's <see cref="BudgetSessions"/> opens.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="dataDirectory">Data directory (tests pass a temporary one).</param>
    /// <param name="logger">Serilog logger; null disables logging (tests).</param>
    /// <param name="configure">Extra service registrations applied after the app's own.</param>
    public static IHost CreateHost(string[] args, IDataDirectory dataDirectory, Serilog.ILogger? logger, Action<IServiceCollection>? configure)
    {
        var sessions = CreateSessions(args, dataDirectory, logger, new JsonAppSettingsStore(dataDirectory), configure);
        var host = CreateSessionHost(args, dataDirectory, logger, sessions, BudgetStartupOptions.Default, configure);
        sessions.Adopt(host);
        return host;
    }

    private static BudgetSessions CreateSessions(string[] args, IDataDirectory dataDirectory, Serilog.ILogger? logger, IAppSettingsStore settings, Action<IServiceCollection>? configure = null)
    {
        BudgetSessions? sessions = null;
        sessions = new BudgetSessions(settings, options => CreateSessionHost(args, dataDirectory, logger, sessions!, options, configure));
        return sessions;
    }

    private static IHost CreateSessionHost(string[] args, IDataDirectory dataDirectory, Serilog.ILogger? logger, BudgetSessions sessions, BudgetStartupOptions options, Action<IServiceCollection>? configure)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            DisableDefaults = true,
            ApplicationName = "Keel",
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        if (logger is not null)
        {
            builder.Services.AddSerilog(logger, dispose: false);
        }

        builder.Services.AddKeelInfrastructure(dataDirectory);
        builder.Services.AddSingleton(sessions.Settings);
        builder.Services.AddSingleton(sessions.KeyRing);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(options);
        builder.Services.AddKeelDesktop();
        configure?.Invoke(builder.Services);
        return builder.Build();
    }
}
