using Avalonia.Input;
using Keel.Application.Files;
using Keel.Application.Settings;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Rules;
using Keel.Desktop.ViewModels.Sync;

namespace Keel.Desktop.ViewModels;

/// <summary>
/// Settings (PRD 9.9): General (budget file, backups, integrity, diagnostics), appearance (theme, accent,
/// density, motion, formats), bank connections, bills and subscriptions, tags, payees, rules, the keyboard
/// shortcut reference generated from the <see cref="ShortcutRegistry"/>, and updates.
/// </summary>
public sealed class SettingsViewModel : PageViewModel, Keel.Application.Navigation.INavigationTarget
{
    private readonly ThemeService _themes;

    /// <summary>Creates the view model.</summary>
    public SettingsViewModel(
        ThemeService themes,
        AppSession session,
        IDataDirectory dataDirectory,
        PlatformShortcuts shortcuts,
        RulesViewModel rules,
        PayeesViewModel payees,
        ConnectionsSettingsViewModel connections,
        Settings.DataFileSettingsViewModel? dataFile = null,
        Settings.AppearanceSettingsViewModel? appearance = null,
        Settings.BillsSettingsViewModel? bills = null,
        Settings.UpdatesSettingsViewModel? updates = null,
        ShortcutRegistry? registry = null,
        Settings.EncryptionSettingsViewModel? encryption = null,
        Settings.StatsSettingsViewModel? stats = null,
        Settings.TagsSettingsViewModel? tags = null)
    {
        Encryption = encryption;
        Stats = stats;
        Tags = tags;
        _themes = themes;
        _themes.ThemeChanged += OnThemeChanged;
        DataFile = dataFile;
        AppearanceExtras = appearance;
        Bills = bills;
        Updates = updates;
        Rules = rules;
        Payees = payees;
        Connections = connections;
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(shortcuts);
        BudgetFilePath = session?.BudgetFile?.Path ?? Strings.Shell_NoFile;
        DataFolderPath = dataDirectory.Root;
        LogsFolderPath = dataDirectory.LogsDirectory;
        registry ??= new ShortcutRegistry(shortcuts);
        Shortcuts = registry.All.Select(e => new ShortcutViewModel(e.Action, e.Keys)).ToList();
        ShortcutGroups = registry.All
            .GroupBy(e => e.Scope)
            .Select(g => new ShortcutGroupViewModel(ShortcutRegistry.ScopeTitle(g.Key), g.Select(e => new ShortcutViewModel(e.Action, e.Keys)).ToList()))
            .ToList();
    }

    /// <inheritdoc />
    public override string Title => Strings.Page_Settings_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Page_Settings_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => Strings.Page_Settings_Title;

    /// <inheritdoc />
    public override string EmptyMessage => Strings.Page_Settings_Subtitle;

    /// <summary>The persisted theme preference.</summary>
    public AppTheme Theme
    {
        get => _themes.Current;
        set
        {
            if (value == _themes.Current)
            {
                return;
            }

            _themes.SetTheme(value);
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    /// <summary>Theme follows the OS.</summary>
    public bool IsSystemTheme
    {
        get => Theme == AppTheme.System;
        set
        {
            if (value)
            {
                Theme = AppTheme.System;
            }
        }
    }

    /// <summary>Theme is light.</summary>
    public bool IsLightTheme
    {
        get => Theme == AppTheme.Light;
        set
        {
            if (value)
            {
                Theme = AppTheme.Light;
            }
        }
    }

    /// <summary>Theme is dark.</summary>
    public bool IsDarkTheme
    {
        get => Theme == AppTheme.Dark;
        set
        {
            if (value)
            {
                Theme = AppTheme.Dark;
            }
        }
    }

    /// <summary>Full path of the open budget file.</summary>
    public string BudgetFilePath { get; }

    /// <summary>Data directory root.</summary>
    public string DataFolderPath { get; }

    /// <summary>Log folder.</summary>
    public string LogsFolderPath { get; }

    /// <summary>Shortcut reference for this platform.</summary>
    public IReadOnlyList<ShortcutViewModel> Shortcuts { get; }

    /// <summary>Settings → Rules (F-TXN-4).</summary>
    public RulesViewModel Rules { get; }

    /// <summary>Settings → Payees (F-TXN-9).</summary>
    public PayeesViewModel Payees { get; }

    /// <summary>Settings → General: the budget file, backups, integrity check and diagnostics (F-SET-1).</summary>
    public Settings.DataFileSettingsViewModel? DataFile { get; }

    /// <summary>Accent, density, motion and formats (F-SET-2).</summary>
    public Settings.AppearanceSettingsViewModel? AppearanceExtras { get; }

    /// <summary>Settings → Bills and subscriptions.</summary>
    public Settings.BillsSettingsViewModel? Bills { get; }

    /// <summary>Settings → Updates.</summary>
    public Settings.UpdatesSettingsViewModel? Updates { get; }

    /// <summary>Settings → Tags (F-TXN-8).</summary>
    public Settings.TagsSettingsViewModel? Tags { get; }

    /// <summary>The shortcut reference grouped by screen.</summary>
    public IReadOnlyList<ShortcutGroupViewModel> ShortcutGroups { get; }

    /// <summary>The section a navigation asked for ("General", "Connections", "Keyboard"…), or null.</summary>
    public string? RequestedSection { get; private set; }

    /// <summary>Raised when <see cref="RequestedSection"/> is set by a navigation.</summary>
    public event EventHandler? SectionRequested;

    /// <inheritdoc />
    public void OnNavigatedTo(object? parameter)
    {
        RequestedSection = parameter as string;
        SectionRequested?.Invoke(this, EventArgs.Empty);
        _ = DataFile?.LoadAsync();
        _ = Bills?.LoadAsync();
        _ = Encryption?.LoadAsync();
        _ = Stats?.LoadAsync();
        _ = Tags?.LoadAsync();
    }

    /// <summary>The Connections section (F-SET-3).</summary>
    public ConnectionsSettingsViewModel Connections { get; }

    /// <summary>Settings → General → Encryption (F-SET-4).</summary>
    public Settings.EncryptionSettingsViewModel? Encryption { get; }

    /// <summary>Settings → Privacy &amp; Stats (PRD 4).</summary>
    public Settings.StatsSettingsViewModel? Stats { get; }
}

/// <summary>One row of the shortcut reference.</summary>
/// <param name="Action">What the shortcut does.</param>
/// <param name="Keys">Platform-specific key text, e.g. "Ctrl+F" or "⌘F".</param>
public sealed record ShortcutViewModel(string Action, string Keys);

/// <summary>A heading of the shortcut reference with its shortcuts.</summary>
/// <param name="Title">Screen or area.</param>
/// <param name="Items">Its shortcuts.</param>
public sealed record ShortcutGroupViewModel(string Title, IReadOnlyList<ShortcutViewModel> Items);
