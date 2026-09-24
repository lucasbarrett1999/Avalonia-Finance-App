using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.Views;

namespace Keel.Desktop.Tests;

/// <summary>Helpers for driving the real UI headlessly.</summary>
internal static class UiTestHelpers
{
    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds (or fails after the timeout).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Timed out waiting: " + because);
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Lets pending loads, messages and layout settle.</summary>
    public static async Task SettleAsync(this AccountsViewModel vm)
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await vm.WhenIdleAsync();
            await Task.Delay(15);
        }

        Dispatcher.UIThread.RunJobs();
    }

    public static T Named<T>(this Visual root, string name)
        where T : Control => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    public static T? FindNamed<T>(this Visual root, string name)
        where T : Control => root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    public static IInputElement? Focused(this Window window) => window.FocusManager?.GetFocusedElement();

    public static void Press(this Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    public static void Type(this Window window, string text)
    {
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    public static RawInputModifiers Command(this PlatformShortcuts shortcuts) =>
        shortcuts.CommandModifiers == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control;

    public static AccountsView Register(this Window window) => window.GetVisualDescendants().OfType<AccountsView>().Single();
}
