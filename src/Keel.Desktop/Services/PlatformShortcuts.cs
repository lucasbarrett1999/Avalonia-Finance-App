using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Keel.Desktop.Services;

/// <summary>
/// Keyboard gestures and their display text for the current platform, derived from Avalonia's
/// <see cref="PlatformHotkeyConfiguration"/>: Cmd on macOS (shown as glyphs), Ctrl elsewhere (PRD 8).
/// </summary>
public sealed class PlatformShortcuts
{
    /// <summary>Creates shortcuts for a platform whose command modifier is <paramref name="commandModifiers"/>.</summary>
    public PlatformShortcuts(KeyModifiers commandModifiers, KeyGesture? undo = null, KeyGesture? redo = null)
    {
        CommandModifiers = commandModifiers;
        Search = new KeyGesture(Key.F, commandModifiers);
        Undo = undo ?? new KeyGesture(Key.Z, commandModifiers);
        Redo = redo ?? new KeyGesture(Key.Z, commandModifiers | KeyModifiers.Shift);
        ToggleSidebar = new KeyGesture(Key.B, commandModifiers);
        FundTargets = new KeyGesture(Key.F, commandModifiers | KeyModifiers.Shift);
    }

    /// <summary>The platform command modifier (Meta on macOS, Control elsewhere).</summary>
    public KeyModifiers CommandModifiers { get; }

    /// <summary>Whether modifiers are displayed as macOS glyphs.</summary>
    public bool UsesMacGlyphs => CommandModifiers == KeyModifiers.Meta;

    /// <summary>Focus the global search box.</summary>
    public KeyGesture Search { get; }

    /// <summary>Undo.</summary>
    public KeyGesture Undo { get; }

    /// <summary>Redo.</summary>
    public KeyGesture Redo { get; }

    /// <summary>Collapse or expand the sidebar.</summary>
    public KeyGesture ToggleSidebar { get; }

    /// <summary>Budget: previous month (Alt+←, ⌥← on macOS).</summary>
    public KeyGesture PreviousMonth { get; } = new(Key.Left, KeyModifiers.Alt);

    /// <summary>Budget: next month (Alt+→).</summary>
    public KeyGesture NextMonth { get; } = new(Key.Right, KeyModifiers.Alt);

    /// <summary>Budget: fund underfunded targets (Ctrl/Cmd+Shift+F).</summary>
    public KeyGesture FundTargets { get; }

    /// <summary>Budget: move money dialog.</summary>
    public KeyGesture MoveMoney { get; } = new(Key.M);

    /// <summary>Budget: set the selected category's target.</summary>
    public KeyGesture SetTarget { get; } = new(Key.T);

    /// <summary>Budget: show or hide the inspector panel.</summary>
    public KeyGesture ToggleInspector { get; } = new(Key.I);

    /// <summary>Budget: quick-assign palette of the selected category.</summary>
    public KeyGesture QuickAssign { get; } = new(Key.Q);

    /// <summary>Ctrl/Cmd+Shift+Z, which redoes everywhere in addition to the platform's own redo gesture.</summary>
    public KeyGesture RedoAlternate => new(Key.Z, CommandModifiers | KeyModifiers.Shift);

    /// <summary>Command palette (Ctrl/Cmd+K, PRD 9.1).</summary>
    public KeyGesture CommandPalette => new(Key.K, CommandModifiers);

    /// <summary>Settings / Preferences (Ctrl/Cmd+,).</summary>
    public KeyGesture Settings => new(Key.OemComma, CommandModifiers);

    /// <summary>Open a budget file (Ctrl/Cmd+O).</summary>
    public KeyGesture OpenFile => new(Key.O, CommandModifiers);

    /// <summary>Sync all linked accounts (Ctrl/Cmd+Shift+S).</summary>
    public KeyGesture SyncAll => new(Key.S, CommandModifiers | KeyModifiers.Shift);

    /// <summary>Quit (Ctrl/Cmd+Q; Windows uses Alt+F4 and shows no gesture).</summary>
    public KeyGesture? Quit => OperatingSystem.IsWindows() ? null : new KeyGesture(Key.Q, CommandModifiers);

    /// <summary>Go to the sidebar page at <paramref name="index"/> (0-based) with Ctrl/Cmd+1…7.</summary>
    public KeyGesture GoTo(int index) => new(Key.D1 + index, CommandModifiers);

    /// <summary>Shortcuts for the running platform.</summary>
    public static PlatformShortcuts FromCurrentPlatform()
    {
        var config = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration;
        return config is null
            ? new PlatformShortcuts(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)
            : new PlatformShortcuts(config.CommandModifiers, config.Undo.FirstOrDefault(), config.Redo.FirstOrDefault());
    }

    /// <summary>Display text, e.g. "Ctrl+Shift+Z" or "⇧⌘Z".</summary>
    public string Format(KeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        var key = KeyText(gesture.Key);
        var mods = gesture.KeyModifiers;
        var text = new StringBuilder();

        if (UsesMacGlyphs)
        {
            if (mods.HasFlag(KeyModifiers.Control))
            {
                text.Append('⌃');
            }

            if (mods.HasFlag(KeyModifiers.Alt))
            {
                text.Append('⌥');
            }

            if (mods.HasFlag(KeyModifiers.Shift))
            {
                text.Append('⇧');
            }

            if (mods.HasFlag(KeyModifiers.Meta))
            {
                text.Append('⌘');
            }

            return text.Append(key).ToString();
        }

        if (mods.HasFlag(KeyModifiers.Control))
        {
            text.Append("Ctrl+");
        }

        if (mods.HasFlag(KeyModifiers.Alt))
        {
            text.Append("Alt+");
        }

        if (mods.HasFlag(KeyModifiers.Shift))
        {
            text.Append("Shift+");
        }

        if (mods.HasFlag(KeyModifiers.Meta))
        {
            text.Append(OperatingSystem.IsWindows() ? "Win+" : "Super+");
        }

        return text.Append(key).ToString();
    }

    private static string KeyText(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Enter => "Enter",
        Key.Escape => "Esc",
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        _ => key.ToString(),
    };
}
