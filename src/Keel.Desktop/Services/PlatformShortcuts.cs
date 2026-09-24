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
        _ => key.ToString(),
    };
}
