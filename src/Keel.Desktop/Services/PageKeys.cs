using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Keel.Desktop.Services;

/// <summary>
/// Page-level single-key shortcuts (Bills, Reports, Goals; see <see cref="ShortcutRegistry"/>): they fire
/// only when no text box, list editor or dialog has the key, so typing never triggers them. Like Budget and
/// Review, the page takes keyboard focus when it is shown, so its keys work right after navigating.
/// </summary>
public static class PageKeys
{
    /// <summary>Handles plain keys on <paramref name="page"/> with <paramref name="handler"/> (returns true when handled).</summary>
    public static void Attach(UserControl page, Func<KeyEventArgs, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(handler);
        page.Focusable = true;
        page.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!e.Handled && !IsTyping(e.Source) && handler(e))
            {
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble);
        page.Loaded += (_, _) => Dispatcher.UIThread.Post(
            () =>
            {
                var focused = TopLevel.GetTopLevel(page)?.FocusManager?.GetFocusedElement() as Visual;
                if (page.IsEffectivelyVisible && (focused is null || !page.IsVisualAncestorOf(focused)) && !InOverlay(focused))
                {
                    page.Focus();
                }
            },
            DispatcherPriority.Background);
    }

    // The search box, a dialog or the notification panel keep their focus.
    private static bool InOverlay(Visual? focused) =>
        focused is TextBox || (focused?.GetVisualAncestors().OfType<Control>().Any(c => c.Name is "DialogLayer" or "NotificationLayer" or "FirstRunHost") ?? false);

    /// <summary>Whether the key goes to a text-entry control.</summary>
    public static bool IsTyping(object? source) => source is TextBox or AutoCompleteBox or NumericUpDown or ComboBox { IsDropDownOpen: true };

    /// <summary>The digit of a number key (1–9), or null.</summary>
    public static int? Digit(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return null;
        }

        return e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D0,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad0,
            _ => null,
        };
    }
}
