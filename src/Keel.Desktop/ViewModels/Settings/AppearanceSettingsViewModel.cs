using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Settings;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Settings;

/// <summary>
/// Settings → Appearance beyond the theme (F-SET-2, PRD 9.11): accent colour, density, reduced motion,
/// and the number/date/currency format (OS locale or an override).
/// </summary>
public sealed partial class AppearanceSettingsViewModel : ViewModelBase
{
    private readonly AppearanceService _appearance;
    private readonly LocaleService _locale;
    private readonly BudgetSessions _sessions;
    private readonly AppSession _session;
    private readonly StatusService _status;
    private bool _loading = true;

    /// <summary>Creates the section.</summary>
    public AppearanceSettingsViewModel(AppearanceService appearance, LocaleService locale, BudgetSessions sessions, AppSession session, StatusService status)
    {
        _appearance = appearance;
        _locale = locale;
        _sessions = sessions;
        _session = session;
        _status = status;
        Accents = AppearanceService.Accents
            .Select(a => new AccentOption(a.Key, Strings.ResourceManager.GetString("Accent_" + a.Key, Strings.Culture) ?? a.Key.ToString(), new SolidColorBrush(a.Value.Light), new SolidColorBrush(a.Value.Dark)))
            .ToList();
        SelectedAccent = Accents.First(a => a.Accent == appearance.Accent);
        MotionChoices = [Strings.Settings_MotionSystem, Strings.Settings_MotionReduce, Strings.Settings_MotionFull];
        MotionIndex = appearance.ReduceMotionSetting switch { null => 0, true => 1, false => 2 };
        var os = LocaleService.OsCulture;
        Cultures = new[] { new CultureOption(null, LedgerText.Format(Strings.Settings_FormatSystem, os.DisplayName), LocaleService.Sample(os)) }
            .Concat(LocaleService.OfferedCultures.Select(name => CultureInfo.GetCultureInfo(name)).Select(c => new CultureOption(c.Name, c.DisplayName, LocaleService.Sample(c))))
            .ToList();
        SelectedCulture = Cultures.FirstOrDefault(c => c.Name == locale.Override) ?? Cultures[0];
        _loading = false;
    }

    /// <summary>Accent choices.</summary>
    public IReadOnlyList<AccentOption> Accents { get; }

    /// <summary>The chosen accent.</summary>
    [ObservableProperty]
    public partial AccentOption SelectedAccent { get; set; }

    /// <summary>Compact density.</summary>
    public bool IsCompact
    {
        get => _appearance.Density == UiDensity.Compact;
        set => SetDensity(value ? UiDensity.Compact : UiDensity.Comfortable);
    }

    /// <summary>Comfortable density.</summary>
    public bool IsComfortable
    {
        get => !IsCompact;
        set
        {
            if (value)
            {
                SetDensity(UiDensity.Comfortable);
            }
        }
    }

    /// <summary>"Follow the system", "Reduce motion", "Allow motion".</summary>
    public IReadOnlyList<string> MotionChoices { get; }

    /// <summary>Index into <see cref="MotionChoices"/>.</summary>
    [ObservableProperty]
    public partial int MotionIndex { get; set; }

    /// <summary>Format cultures: the OS first, then overrides, each with a sample.</summary>
    public IReadOnlyList<CultureOption> Cultures { get; }

    /// <summary>The chosen format culture.</summary>
    [ObservableProperty]
    public partial CultureOption SelectedCulture { get; set; }

    /// <summary>Sample of the chosen format.</summary>
    public string FormatSample => SelectedCulture.Sample;

    /// <summary>Saves and applies a density.</summary>
    public void SetDensity(UiDensity density)
    {
        _appearance.SetDensity(density, _sessions.Window);
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(IsComfortable));
    }

    partial void OnSelectedAccentChanged(AccentOption value)
    {
        if (!_loading && value is not null)
        {
            _appearance.SetAccent(value.Accent);
        }
    }

    partial void OnMotionIndexChanged(int value)
    {
        if (!_loading)
        {
            _appearance.SetReduceMotion(value switch { 1 => true, 2 => false, _ => null }, _sessions.Window);
        }
    }

    partial void OnSelectedCultureChanged(CultureOption value)
    {
        OnPropertyChanged(nameof(FormatSample));
        if (_loading || value is null || value.Name == _locale.Override)
        {
            return;
        }

        _locale.SetOverride(value.Name);
        if (_session.BudgetFile is { } file)
        {
            // Reopen the file so every screen formats with the new culture.
            _ = ReopenAsync(file.Path);
        }
    }

    private async Task ReopenAsync(string path)
    {
        try
        {
            await _sessions.OpenAsync(path, new BudgetStartupOptions(Message: Strings.Settings_FormatApplied));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _status.Show(ex.Message, isError: true);
        }
    }
}

/// <summary>An accent colour choice.</summary>
/// <param name="Accent">Accent.</param>
/// <param name="Name">Localized name (never colour alone).</param>
/// <param name="LightBrush">Swatch in the light theme.</param>
/// <param name="DarkBrush">Swatch in the dark theme.</param>
public sealed record AccentOption(AppAccent Accent, string Name, IBrush LightBrush, IBrush DarkBrush);

/// <summary>A format culture choice.</summary>
/// <param name="Name">Culture name, or null for the OS.</param>
/// <param name="DisplayName">Shown name.</param>
/// <param name="Sample">"-$12,345.67 · 1,234,567.89 · 9/24/2026".</param>
public sealed record CultureOption(string? Name, string DisplayName, string Sample)
{
    /// <summary>List text.</summary>
    public string Label => DisplayName + " — " + Sample;
}
