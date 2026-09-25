namespace Keel.Application.Settings;

/// <summary>Theme preference (F-SET-2).</summary>
public enum AppTheme
{
    /// <summary>Follow the operating system.</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>
/// App-level settings stored in <c>settings.json</c> in the data directory (PRD 7.3).
/// Settings that belong with a budget file live in its <c>Setting</c> table instead.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Current schema version of the file.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version of the file.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Theme preference.</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>Budget file opened last; reopened on launch.</summary>
    public string? LastBudgetFile { get; init; }

    /// <summary>Window placement per display configuration key (PRD 8).</summary>
    public IReadOnlyDictionary<string, WindowPlacement> WindowPlacements { get; init; } =
        new Dictionary<string, WindowPlacement>(StringComparer.Ordinal);

    /// <summary>The first-run setup (PRD 9.10) finished or was skipped; it is never shown again.</summary>
    public bool FirstRunCompleted { get; init; }

    /// <summary>Accent colour (F-SET-2).</summary>
    public AppAccent Accent { get; init; } = AppAccent.Teal;

    /// <summary>Spacing density (F-SET-2).</summary>
    public UiDensity Density { get; init; } = UiDensity.Comfortable;

    /// <summary>Culture used for numbers, dates and currency, e.g. "en-GB"; null follows the OS (F-SET-2).</summary>
    public string? FormatCulture { get; init; }

    /// <summary>Reduce motion: null follows the OS where it can be read, otherwise the explicit choice (PRD 9.11).</summary>
    public bool? ReduceMotion { get; init; }

    /// <summary>Take an automatic backup of the open file once a day (F-SET-1).</summary>
    public bool AutoBackupEnabled { get; init; } = true;

    /// <summary>Number of automatic backups kept per budget file.</summary>
    public int AutoBackupKeep { get; init; } = 10;

    /// <summary>Check for updates on start (PRD 10: off by default until a release feed exists).</summary>
    public bool CheckForUpdates { get; init; }

    /// <summary>Returns a copy with the placement for <paramref name="displayKey"/> replaced.</summary>
    public AppSettings WithWindowPlacement(string displayKey, WindowPlacement placement)
    {
        var placements = new Dictionary<string, WindowPlacement>(WindowPlacements, StringComparer.Ordinal)
        {
            [displayKey] = placement,
        };
        return this with { WindowPlacements = placements };
    }
}

/// <summary>Main-window size, position and sidebar state for one display configuration.</summary>
/// <param name="Width">Window width in device-independent pixels.</param>
/// <param name="Height">Window height in device-independent pixels.</param>
/// <param name="X">Left edge in screen pixels, or null to let the OS place the window.</param>
/// <param name="Y">Top edge in screen pixels, or null to let the OS place the window.</param>
/// <param name="IsMaximized">Whether the window was maximized.</param>
/// <param name="SidebarWidth">Expanded sidebar width.</param>
/// <param name="IsSidebarCollapsed">Whether the sidebar shows icons only.</param>
public sealed record WindowPlacement(
    double Width,
    double Height,
    int? X,
    int? Y,
    bool IsMaximized,
    double SidebarWidth,
    bool IsSidebarCollapsed);

/// <summary>Accent colours offered in Settings → Appearance (each has AA-contrast light and dark variants).</summary>
public enum AppAccent
{
    /// <summary>Keel's default teal.</summary>
    Teal,

    /// <summary>Blue.</summary>
    Blue,

    /// <summary>Violet.</summary>
    Violet,

    /// <summary>Green.</summary>
    Green,

    /// <summary>Orange.</summary>
    Orange,

    /// <summary>Rose.</summary>
    Rose,
}

/// <summary>Spacing density (F-SET-2).</summary>
public enum UiDensity
{
    /// <summary>Default spacing.</summary>
    Comfortable,

    /// <summary>Tighter rows and padding for more data on screen.</summary>
    Compact,
}
