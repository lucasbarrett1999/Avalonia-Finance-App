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
