using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Keel.Application.Settings;

namespace Keel.Desktop.Services;

/// <summary>
/// Accent colour, density and reduced motion (F-SET-2, PRD 9.11). The accent replaces the Fluent
/// palette accent and Keel's accent tokens in both theme dictionaries; density and reduced motion are
/// style classes on the main window (<c>compact</c>, <c>reduceMotion</c>, see Styles/Density.axaml).
/// </summary>
public sealed class AppearanceService(IAppSettingsStore settings)
{
    private static bool? _osReduceMotion;

    /// <summary>Light and dark accent colours per choice; each passes WCAG AA with the badge text and on page backgrounds.</summary>
    public static IReadOnlyDictionary<AppAccent, (Color Light, Color Dark)> Accents { get; } = new Dictionary<AppAccent, (Color, Color)>
    {
        [AppAccent.Teal] = (Color.Parse("#0B6477"), Color.Parse("#3FB5CC")),
        [AppAccent.Blue] = (Color.Parse("#1D5FBF"), Color.Parse("#6FA8FF")),
        [AppAccent.Violet] = (Color.Parse("#6A3FB5"), Color.Parse("#B69CFF")),
        [AppAccent.Green] = (Color.Parse("#1E7040"), Color.Parse("#5FCB8E")),
        [AppAccent.Orange] = (Color.Parse("#A34B00"), Color.Parse("#F5A55A")),
        [AppAccent.Rose] = (Color.Parse("#B0284F"), Color.Parse("#FF8FAE")),
    };

    /// <summary>Resource keys that carry the accent (theme dictionaries of Styles/Tokens.axaml).</summary>
    public static IReadOnlyList<string> AccentKeys { get; } =
        ["Keel.Accent", "Keel.BadgeBackground", "Keel.IconCircleForeground", "Keel.Budget.Cursor", "Keel.Bills.TodayBorder", "Keel.Bills.UnreadDot", "Keel.Chart.RingFill"];

    private static readonly Dictionary<(ResourceDictionary Theme, string Key), object?> Originals = [];

    /// <summary>The saved accent.</summary>
    public AppAccent Accent => settings.Current.Accent;

    /// <summary>The saved density.</summary>
    public UiDensity Density => settings.Current.Density;

    /// <summary>The saved reduce-motion choice (null = follow the OS).</summary>
    public bool? ReduceMotionSetting => settings.Current.ReduceMotion;

    /// <summary>Whether motion is reduced now (the explicit choice, else the OS setting where Keel can read it).</summary>
    public bool IsMotionReduced => settings.Current.ReduceMotion ?? OsPrefersReducedMotion();

    /// <summary>Applies accent, density and motion (startup).</summary>
    public void ApplySaved(Window? window)
    {
        ApplyAccent(settings.Current.Accent);
        ApplyWindowClasses(window);
    }

    /// <summary>Saves and applies an accent.</summary>
    public void SetAccent(AppAccent accent)
    {
        settings.Update(s => s with { Accent = accent });
        ApplyAccent(accent);
    }

    /// <summary>Saves a density and applies it to <paramref name="window"/>.</summary>
    public void SetDensity(UiDensity density, Window? window)
    {
        settings.Update(s => s with { Density = density });
        ApplyWindowClasses(window);
    }

    /// <summary>Saves a reduce-motion choice (null = OS) and applies it to <paramref name="window"/>.</summary>
    public void SetReduceMotion(bool? reduce, Window? window)
    {
        settings.Update(s => s with { ReduceMotion = reduce });
        ApplyWindowClasses(window);
    }

    /// <summary>Sets the <c>compact</c> and <c>reduceMotion</c> classes of the main window.</summary>
    public void ApplyWindowClasses(Window? window)
    {
        if (window is null)
        {
            return;
        }

        window.Classes.Set("compact", settings.Current.Density == UiDensity.Compact);
        window.Classes.Set("reduceMotion", IsMotionReduced);
    }

    /// <summary>Replaces the accent in the Fluent palettes and Keel's tokens.</summary>
    public static void ApplyAccent(AppAccent accent)
    {
        if (Avalonia.Application.Current is not { } app || !Accents.TryGetValue(accent, out var colors))
        {
            return;
        }

        foreach (var fluent in app.Styles.OfType<FluentTheme>())
        {
            if (fluent.Palettes.TryGetValue(ThemeVariant.Light, out var light))
            {
                light.Accent = colors.Light;
            }

            if (fluent.Palettes.TryGetValue(ThemeVariant.Dark, out var dark))
            {
                dark.Accent = colors.Dark;
            }
        }

        var dictionaries = app.Resources.MergedDictionaries.OfType<ResourceDictionary>()
            .Concat(app.Styles.OfType<Avalonia.Markup.Xaml.Styling.StyleInclude>().Select(i => i.Loaded).OfType<Styles>()
                .Select(st => st.Resources).OfType<ResourceDictionary>())
            .ToList();
        foreach (var dictionary in dictionaries)
        {
            SetTokens(dictionary, ThemeVariant.Light, accent == AppAccent.Teal ? null : colors.Light);
            SetTokens(dictionary, ThemeVariant.Dark, accent == AppAccent.Teal ? null : colors.Dark);
        }
    }

    /// <summary>
    /// Reads the OS "reduce motion" preference where it is available without platform code: GNOME's
    /// <c>enable-animations</c> on Linux and the accessibility default on macOS. Windows users choose in
    /// Settings (ADR 0082). Read once per process; false when unknown.
    /// </summary>
    public static bool OsPrefersReducedMotion()
    {
        if (_osReduceMotion is { } known)
        {
            return known;
        }

        var reduce = false;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                reduce = Run("gsettings", "get org.gnome.desktop.interface enable-animations")?.Trim() == "false";
            }
            else if (OperatingSystem.IsMacOS())
            {
                reduce = Run("defaults", "read com.apple.universalaccess reduceMotion")?.Trim() == "1";
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            reduce = false;
        }

        _osReduceMotion = reduce;
        return reduce;
    }

    // Teal (null) restores the designed tokens, which use lighter tints of the accent in places.
    private static void SetTokens(ResourceDictionary dictionary, ThemeVariant variant, Color? color)
    {
        if (!dictionary.ThemeDictionaries.TryGetValue(variant, out var provider) || provider is not ResourceDictionary theme)
        {
            return;
        }

        foreach (var key in AccentKeys)
        {
            if (!theme.TryGetValue(key, out var current))
            {
                continue;
            }

            if (!Originals.ContainsKey((theme, key)))
            {
                Originals[(theme, key)] = current;
            }

            theme[key] = color is { } c ? new SolidColorBrush(c) : Originals[(theme, key)];
        }
    }

    private static string? Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEnd();
        return process.WaitForExit(1500) && process.ExitCode == 0 ? output : null;
    }
}
