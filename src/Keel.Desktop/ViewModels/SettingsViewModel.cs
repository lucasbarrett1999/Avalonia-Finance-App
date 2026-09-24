using Avalonia.Input;
using Keel.Application.Files;
using Keel.Application.Settings;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels;

/// <summary>Settings (PRD 9.9): appearance, data locations, and the shortcut reference.</summary>
public sealed class SettingsViewModel : PageViewModel
{
    private readonly ThemeService _themes;

    /// <summary>Creates the view model.</summary>
    public SettingsViewModel(ThemeService themes, AppSession session, IDataDirectory dataDirectory, PlatformShortcuts shortcuts)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(shortcuts);
        _themes = themes;
        BudgetFilePath = session?.BudgetFile?.Path ?? Strings.Shell_NoFile;
        DataFolderPath = dataDirectory.Root;
        LogsFolderPath = dataDirectory.LogsDirectory;
        Shortcuts =
        [
            new ShortcutViewModel(Strings.Shortcut_Search, shortcuts.Format(shortcuts.Search)),
            new ShortcutViewModel(Strings.Shortcut_Undo, shortcuts.Format(shortcuts.Undo)),
            new ShortcutViewModel(Strings.Shortcut_Redo, shortcuts.Format(shortcuts.Redo)),
            new ShortcutViewModel(Strings.Shortcut_ToggleSidebar, shortcuts.Format(shortcuts.ToggleSidebar)),
            new ShortcutViewModel(Strings.Shortcut_NewTransaction, "N"),
            new ShortcutViewModel(Strings.Shortcut_EditTransaction, shortcuts.Format(new KeyGesture(Key.Enter))),
            new ShortcutViewModel(Strings.Shortcut_ToggleCleared, "C"),
            new ShortcutViewModel(Strings.Shortcut_Approve, "A"),
            new ShortcutViewModel(Strings.Shortcut_Delete, shortcuts.Format(new KeyGesture(Key.Delete))),
            new ShortcutViewModel(Strings.Shortcut_SaveAndNew, shortcuts.Format(new KeyGesture(Key.Enter, shortcuts.CommandModifiers))),
            new ShortcutViewModel(Strings.Shortcut_Cancel, shortcuts.Format(new KeyGesture(Key.Escape))),
            new ShortcutViewModel(Strings.Shortcut_BudgetPreviousMonth, shortcuts.Format(shortcuts.PreviousMonth)),
            new ShortcutViewModel(Strings.Shortcut_BudgetNextMonth, shortcuts.Format(shortcuts.NextMonth)),
            new ShortcutViewModel(Strings.Shortcut_BudgetNavigate, "↑ ↓ ← →"),
            new ShortcutViewModel(Strings.Shortcut_BudgetEdit, shortcuts.Format(new KeyGesture(Key.Enter))),
            new ShortcutViewModel(Strings.Shortcut_BudgetNextAssigned, shortcuts.Format(new KeyGesture(Key.Tab))),
            new ShortcutViewModel(Strings.Shortcut_BudgetMoveMoney, shortcuts.Format(shortcuts.MoveMoney)),
            new ShortcutViewModel(Strings.Shortcut_BudgetSetTarget, shortcuts.Format(shortcuts.SetTarget)),
            new ShortcutViewModel(Strings.Shortcut_BudgetFundTargets, shortcuts.Format(shortcuts.FundTargets)),
            new ShortcutViewModel(Strings.Shortcut_BudgetInspector, shortcuts.Format(shortcuts.ToggleInspector)),
            new ShortcutViewModel(Strings.Shortcut_BudgetQuickAssign, shortcuts.Format(shortcuts.QuickAssign)),
        ];
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
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSystemTheme));
            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(IsDarkTheme));
        }
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
}

/// <summary>One row of the shortcut reference.</summary>
/// <param name="Action">What the shortcut does.</param>
/// <param name="Keys">Platform-specific key text, e.g. "Ctrl+F" or "⌘F".</param>
public sealed record ShortcutViewModel(string Action, string Keys);
