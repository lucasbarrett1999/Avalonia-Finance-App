using Avalonia;
using Keel.Application.Files;
using Keel.Desktop.Services;
using Keel.Infrastructure;
using Keel.Infrastructure.Files;
using Keel.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Keel.Desktop;

/// <summary>Entry point and composition root. The only place that references Keel.Infrastructure.</summary>
internal static class Program
{
    /// <summary>Starts the generic host, opens the budget file, and runs the Avalonia app.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var dataDirectory = DataDirectory.ForCurrentUser();
        dataDirectory.EnsureCreated();
        using var logger = KeelLogging.CreateLogger(dataDirectory);

        try
        {
            logger.Information("Keel starting");
            using var host = CreateHost(args, dataDirectory, logger);
            host.Start();

            host.Services.GetRequiredService<BudgetFileStartup>().OpenInitialFileAsync(CancellationToken.None).GetAwaiter().GetResult();

            App.Services = host.Services;
            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
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

    /// <summary>Builds the generic host with DI for infrastructure and desktop services.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="dataDirectory">Data directory (tests pass a temporary one).</param>
    /// <param name="logger">Serilog logger; null disables logging (tests).</param>
    public static IHost CreateHost(string[] args, IDataDirectory dataDirectory, Serilog.ILogger? logger)
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
        builder.Services.AddKeelDesktop();
        return builder.Build();
    }
}
