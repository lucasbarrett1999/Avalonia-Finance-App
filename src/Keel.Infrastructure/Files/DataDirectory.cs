using Keel.Application.Files;

namespace Keel.Infrastructure.Files;

/// <summary>Operating systems with distinct data-directory conventions.</summary>
public enum DataDirectoryPlatform
{
    /// <summary>Windows: <c>%APPDATA%\Keel</c>.</summary>
    Windows,

    /// <summary>macOS: <c>~/Library/Application Support/Keel</c>.</summary>
    MacOS,

    /// <summary>Linux and other Unix: <c>$XDG_DATA_HOME/keel</c>, default <c>~/.local/share/keel</c>.</summary>
    Linux,
}

/// <summary>The per-user data directory (PRD 7.4).</summary>
public sealed class DataDirectory : IDataDirectory
{
    /// <summary>Creates a data directory rooted at <paramref name="root"/>.</summary>
    public DataDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <inheritdoc />
    public string Root { get; }

    /// <inheritdoc />
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <inheritdoc />
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <inheritdoc />
    public string SecretsDirectory => Path.Combine(Root, "secrets");

    /// <inheritdoc />
    public string BudgetsDirectory => Path.Combine(Root, "budgets");

    /// <inheritdoc />
    public string BackupsDirectory => Path.Combine(BudgetsDirectory, "backups");

    /// <inheritdoc />
    public string DefaultBudgetFile => Path.Combine(BudgetsDirectory, "Default" + IBudgetFileService.Extension);

    /// <summary>The data directory of the current user on the current OS.</summary>
    public static DataDirectory ForCurrentUser() =>
        new(ResolveDefaultRoot(CurrentPlatform, Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    /// <summary>The platform this process runs on.</summary>
    public static DataDirectoryPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? DataDirectoryPlatform.Windows
        : OperatingSystem.IsMacOS() ? DataDirectoryPlatform.MacOS
        : DataDirectoryPlatform.Linux;

    /// <summary>Resolves the data-directory root for a platform (pure; used by tests for every OS).</summary>
    /// <param name="platform">Target OS.</param>
    /// <param name="getEnvironmentVariable">Environment lookup.</param>
    /// <param name="home">The user's home directory.</param>
    public static string ResolveDefaultRoot(DataDirectoryPlatform platform, Func<string, string?> getEnvironmentVariable, string home)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        switch (platform)
        {
            case DataDirectoryPlatform.Windows:
                var appData = getEnvironmentVariable("APPDATA");
                return Path.Combine(string.IsNullOrWhiteSpace(appData) ? Path.Combine(home, "AppData", "Roaming") : appData, "Keel");
            case DataDirectoryPlatform.MacOS:
                return Path.Combine(home, "Library", "Application Support", "Keel");
            case DataDirectoryPlatform.Linux:
                var xdg = getEnvironmentVariable("XDG_DATA_HOME");
                var dataHome = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".local", "share");
                return Path.Combine(dataHome, "keel");
            default:
                throw new ArgumentOutOfRangeException(nameof(platform), platform, null);
        }
    }

    /// <inheritdoc />
    public string AttachmentsDirectoryFor(string budgetFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(budgetFile);
        var full = Path.GetFullPath(budgetFile);
        var directory = Path.GetDirectoryName(full) ?? Root;
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(full) + IBudgetFileService.Extension + "-attachments");
    }

    /// <inheritdoc />
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(BudgetsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}
