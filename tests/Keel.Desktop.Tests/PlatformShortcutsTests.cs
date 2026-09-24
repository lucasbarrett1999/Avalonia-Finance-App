using Avalonia.Input;
using Keel.Desktop.Services;

namespace Keel.Desktop.Tests;

public class PlatformShortcutsTests
{
    [Fact]
    public void Uses_ctrl_text_on_windows_and_linux()
    {
        var shortcuts = new PlatformShortcuts(KeyModifiers.Control);
        shortcuts.UsesMacGlyphs.ShouldBeFalse();
        shortcuts.Format(shortcuts.Search).ShouldBe("Ctrl+F");
        shortcuts.Format(shortcuts.Redo).ShouldBe("Ctrl+Shift+Z");
        shortcuts.Format(new KeyGesture(Key.D1, KeyModifiers.Control | KeyModifiers.Alt)).ShouldBe("Ctrl+Alt+1");
    }

    [Fact]
    public void Uses_command_glyphs_on_macos()
    {
        var shortcuts = new PlatformShortcuts(KeyModifiers.Meta);
        shortcuts.UsesMacGlyphs.ShouldBeTrue();
        shortcuts.Search.KeyModifiers.ShouldBe(KeyModifiers.Meta);
        shortcuts.Format(shortcuts.Search).ShouldBe("⌘F");
        shortcuts.Format(shortcuts.Redo).ShouldBe("⇧⌘Z");
    }

    [Fact]
    public void Honors_platform_undo_and_redo_gestures()
    {
        var shortcuts = new PlatformShortcuts(KeyModifiers.Control, redo: new KeyGesture(Key.Y, KeyModifiers.Control));
        shortcuts.Format(shortcuts.Redo).ShouldBe("Ctrl+Y");
        shortcuts.Format(shortcuts.Undo).ShouldBe("Ctrl+Z");
    }
}
