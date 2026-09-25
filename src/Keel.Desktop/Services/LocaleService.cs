using System.Globalization;
using Keel.Application.Settings;

namespace Keel.Desktop.Services;

/// <summary>
/// Number, date and currency formats (F-SET-2): they follow the OS locale unless Settings names another
/// culture. Only the formatting culture changes; UI text stays in the app's language. The culture is
/// applied at startup and, after a change, by reopening the session so every screen reformats.
/// </summary>
public sealed class LocaleService(IAppSettingsStore settings)
{
    private static CultureInfo? _osCulture;

    /// <summary>Cultures offered as overrides (null = follow the OS).</summary>
    public static IReadOnlyList<string> OfferedCultures { get; } =
    [
        "en-US", "en-GB", "en-CA", "en-AU", "en-IE", "en-NZ", "de-DE", "fr-FR", "fr-CA", "es-ES", "es-MX", "it-IT",
        "nl-NL", "pt-BR", "pt-PT", "sv-SE", "da-DK", "nb-NO", "fi-FI", "pl-PL", "de-CH", "ja-JP",
    ];

    /// <summary>The OS culture Keel started with (before any override).</summary>
    public static CultureInfo OsCulture => _osCulture ?? CultureInfo.CurrentCulture;

    /// <summary>The saved override, or null.</summary>
    public string? Override => settings.Current.FormatCulture;

    /// <summary>Resolves an override name to a culture; unknown or empty names follow the OS.</summary>
    public static CultureInfo Resolve(string? name, CultureInfo osCulture)
    {
        ArgumentNullException.ThrowIfNull(osCulture);
        if (string.IsNullOrWhiteSpace(name))
        {
            return osCulture;
        }

        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return osCulture;
        }
    }

    /// <summary>Applies the formatting culture process-wide (startup and after a change).</summary>
    public static void ApplyCulture(string? name)
    {
        _osCulture ??= CultureInfo.CurrentCulture;
        var culture = Resolve(name, _osCulture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
    }

    /// <summary>Saves a new override (null = OS) and applies it; the caller reopens the session.</summary>
    public void SetOverride(string? name)
    {
        settings.Update(s => s with { FormatCulture = string.IsNullOrWhiteSpace(name) ? null : name });
        ApplyCulture(name);
    }

    /// <summary>Sample of the formats a culture produces, for the Settings preview.</summary>
    public static string Sample(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var money = new Keel.Domain.Money(-1_234_567, "USD").Format(culture);
        var date = new DateOnly(2026, 9, 24).ToString("d", culture);
        var number = 1234567.89m.ToString("N2", culture);
        return $"{money} · {number} · {date}";
    }
}
