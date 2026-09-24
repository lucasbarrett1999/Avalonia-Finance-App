using Avalonia;
using Avalonia.Headless;
using Keel.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Keel.Desktop.Tests;

/// <summary>
/// Runs the real <see cref="App"/> (styles, theme, view locator, Inter font) on the headless
/// platform with Skia rendering, so frames can be captured.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .AfterSetup(_ => Avalonia.Logging.Logger.Sink = LogCapture.Instance);
}
