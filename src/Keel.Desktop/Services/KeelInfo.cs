using System.Reflection;

namespace Keel.Desktop.Services;

/// <summary>Version and project links shown in About, the diagnostic bundle and the update check.</summary>
public static class KeelInfo
{
    /// <summary>Project home (source, issues, releases).</summary>
    public static Uri Repository { get; } = new("https://github.com/lucasbarrett1999/Avalonia-Finance-App");

    /// <summary>The user guide.</summary>
    public static Uri UserGuide { get; } = new("https://github.com/lucasbarrett1999/Avalonia-Finance-App/blob/main/docs/user-guide/README.md");

    /// <summary>Downloads (GitHub releases; also the Velopack update feed).</summary>
    public static Uri Releases { get; } = new("https://github.com/lucasbarrett1999/Avalonia-Finance-App/releases");

    /// <summary>The product version, e.g. "1.0.0-rc.1" (from Directory.Build.props, without the commit hash).</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(KeelInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(KeelInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
