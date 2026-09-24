using Avalonia.Styling;
using Keel.Application.Settings;

namespace Keel.Desktop.Services;

/// <summary>Applies the light/dark/system theme and persists the choice in settings.json.</summary>
public sealed class ThemeService(IAppSettingsStore settings)
{
    /// <summary>The persisted theme preference.</summary>
    public AppTheme Current => settings.Current.Theme;

    /// <summary>Maps a preference to an Avalonia theme variant (System follows the OS).</summary>
    public static ThemeVariant ToVariant(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>Applies the persisted preference (at startup).</summary>
    public void ApplySaved() => Apply(settings.Current.Theme);

    /// <summary>Applies and persists a new preference.</summary>
    public void SetTheme(AppTheme theme)
    {
        if (settings.Current.Theme != theme)
        {
            settings.Update(s => s with { Theme = theme });
        }

        Apply(theme);
    }

    private static void Apply(AppTheme theme)
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = ToVariant(theme);
        }
    }
}
