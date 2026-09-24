using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Keel.Application.Settings;

namespace Keel.Desktop.Services;

/// <summary>
/// Persists main-window size, position, maximized state and sidebar state per display
/// configuration (PRD 8), in settings.json.
/// </summary>
public sealed class WindowPlacementService(IAppSettingsStore settings)
{
    /// <summary>Key used when the platform reports no screens (e.g. headless).</summary>
    public const string NoScreensKey = "default";

    /// <summary>Default expanded sidebar width.</summary>
    public const double DefaultSidebarWidth = 232;

    /// <summary>Identifies the current arrangement of monitors.</summary>
    public static string DisplayKey(IReadOnlyList<Screen>? screens)
    {
        if (screens is null || screens.Count == 0)
        {
            return NoScreensKey;
        }

        return string.Join(
            ";",
            screens
                .OrderBy(s => s.Bounds.X)
                .ThenBy(s => s.Bounds.Y)
                .Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width}x{s.Bounds.Height}@{s.Scaling:0.##}")));
    }

    /// <summary>Returns the saved placement for the window's current display configuration.</summary>
    public WindowPlacement? Find(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return settings.Current.WindowPlacements.TryGetValue(DisplayKey(window.Screens?.All), out var placement) ? placement : null;
    }

    /// <summary>Applies a saved placement before the window is shown.</summary>
    public static void Apply(Window window, WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(placement);

        window.Width = Math.Max(placement.Width, window.MinWidth);
        window.Height = Math.Max(placement.Height, window.MinHeight);

        if (placement.X is { } x && placement.Y is { } y && IsOnScreen(window.Screens?.All, new PixelPoint(x, y)))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(x, y);
        }

        if (placement.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Saves a placement for the window's current display configuration.</summary>
    public void Save(Window window, WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(window);
        var key = DisplayKey(window.Screens?.All);
        settings.Update(s => s.WithWindowPlacement(key, placement));
    }

    private static bool IsOnScreen(IReadOnlyList<Screen>? screens, PixelPoint topLeft)
    {
        if (screens is null || screens.Count == 0)
        {
            return false;
        }

        // Require the title-bar area to be visible on some monitor.
        var probe = new PixelPoint(topLeft.X + 48, topLeft.Y + 16);
        return screens.Any(s => s.WorkingArea.Contains(probe));
    }
}
