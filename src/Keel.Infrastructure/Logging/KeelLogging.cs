using Keel.Application.Files;
using Serilog;
using Serilog.Events;

namespace Keel.Infrastructure.Logging;

/// <summary>
/// Serilog configuration: a daily rolling file in <c>&lt;datadir&gt;/logs</c>. Log messages must
/// never contain payee names, amounts, or secrets (PRD 10).
/// </summary>
public static class KeelLogging
{
    /// <summary>Log file name pattern; Serilog inserts the date before the extension.</summary>
    public const string FileNamePattern = "keel-.log";

    /// <summary>Creates the application logger.</summary>
    /// <param name="dataDirectory">Data directory whose logs folder receives the files.</param>
    /// <param name="minimumLevel">Minimum level; Information by default.</param>
    public static Serilog.Core.Logger CreateLogger(IDataDirectory dataDirectory, LogEventLevel minimumLevel = LogEventLevel.Information)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        Directory.CreateDirectory(dataDirectory.LogsDirectory);

        return new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(dataDirectory.LogsDirectory, FileNamePattern),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
