using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.Views;

namespace Keel.Desktop.Tests;

/// <summary>
/// Renders the M9 stream E screens in light and dark without binding or resource errors: Settings → Privacy &amp;
/// Stats, the Encryption section, the encrypt, unlock and remove-encryption dialogs, and the palette with unavailable
/// commands. With KEEL_SCREENSHOT_DIR set the frames are saved for review.
/// </summary>
public sealed class M9eRenderingTests
{
    [AvaloniaFact]
    public async Task Encryption_stats_and_palette_render_in_light_and_dark()
    {
        LogCapture.Instance.Clear();
        using var host = TestHost.Create();
        await M9eScreens.SeedAsync(host);
        var window = host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = host.Get<ThemeService>();
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            await M9eScreens.VisitAsync(host, shell, screen =>
            {
                using var frame = window.CaptureRenderedFrame()!;
                frame.PixelSize.Width.ShouldBeGreaterThan(0);
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                    frame.Save(Path.Combine(outputDir, $"{screen}-{theme}.png"));
                }

                return Task.CompletedTask;
            });
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }
}
