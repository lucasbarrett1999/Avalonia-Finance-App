using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Settings;
using Keel.Desktop.Controls;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.Views;

namespace Keel.Desktop.Tests;

public sealed class RenderingTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    [AvaloniaFact]
    public void Every_screen_renders_in_light_and_dark_without_binding_or_resource_errors()
    {
        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        var themes = _host.Get<ThemeService>();
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            foreach (var item in shell.AllItems)
            {
                item.NavigateCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame();
                frame.ShouldNotBeNull();
                frame.PixelSize.Width.ShouldBeGreaterThan(0);

                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                    frame.Save(Path.Combine(outputDir, $"{item.PageType.Name.Replace("ViewModel", string.Empty, StringComparison.Ordinal)}-{theme}.png"));
                }
            }
        }

        // Every sidebar icon resolved to a geometry.
        window.GetVisualDescendants().OfType<Icon>().ShouldAllBe(i => i.Data != null);

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public void Light_and_dark_frames_differ()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var themes = _host.Get<ThemeService>();

        themes.SetTheme(AppTheme.Light);
        Dispatcher.UIThread.RunJobs();
        using var light = window.CaptureRenderedFrame()!;
        var lightPixel = CenterPixel(light);

        themes.SetTheme(AppTheme.Dark);
        Dispatcher.UIThread.RunJobs();
        using var dark = window.CaptureRenderedFrame()!;
        var darkPixel = CenterPixel(dark);

        // The page background is near-white in light and near-black in dark.
        Luminance(lightPixel).ShouldBeGreaterThan(200);
        Luminance(darkPixel).ShouldBeLessThan(60);

        themes.SetTheme(AppTheme.System);
        window.Close();
    }

    private static byte[] CenterPixel(WriteableBitmap bitmap)
    {
        using var locked = bitmap.Lock();
        var x = bitmap.PixelSize.Width - 40;
        var y = bitmap.PixelSize.Height / 2;
        var bytes = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(locked.Address + (y * locked.RowBytes) + (x * 4), bytes, 0, 4);
        return bytes; // BGRA
    }

    private static double Luminance(byte[] bgra) => (0.0722 * bgra[0]) + (0.7152 * bgra[1]) + (0.2126 * bgra[2]);
}
