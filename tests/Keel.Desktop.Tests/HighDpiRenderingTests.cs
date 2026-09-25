using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.Views;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Desktop.Tests;

/// <summary>
/// 200% scaling (PRD 11): every screen renders at 2x device pixels (192 DPI) without binding or resource
/// errors, including the smallest logical window a 1080p display offers at 200% (960 × 540).
/// </summary>
public sealed class HighDpiRenderingTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    [AvaloniaFact]
    public async Task Every_screen_renders_at_2x_scaling_on_a_1080p_display()
    {
        await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(_host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(1_000, Seed: 3, EndDate: DateOnly.FromDateTime(DateTime.Today)), CancellationToken.None));
        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.MinWidth.ShouldBeLessThanOrEqualTo(960);
        window.MinHeight.ShouldBeLessThanOrEqualTo(540);
        window.Width = 960;
        window.Height = 540;
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = _host.Get<ThemeService>();
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            foreach (var item in shell.AllItems)
            {
                item.NavigateCommand.Execute(null);
                for (var i = 0; i < 6; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(20);
                }

                using var bitmap = new RenderTargetBitmap(new PixelSize(1920, 1080), new Vector(192, 192));
                bitmap.Render(window);
                bitmap.PixelSize.ShouldBe(new PixelSize(1920, 1080));
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                    using (var frame = window.CaptureRenderedFrame()!)
                    {
                        frame.Save(Path.Combine(outputDir, $"{item.PageType.Name.Replace("ViewModel", string.Empty, StringComparison.Ordinal)}-narrow-{theme}.png"));
                    }

                    bitmap.Save(Path.Combine(outputDir, $"{item.PageType.Name.Replace("ViewModel", string.Empty, StringComparison.Ordinal)}-2x-{theme}.png"));
                }
            }
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task Encryption_and_stats_render_at_2x_scaling_on_a_1080p_display()
    {
        await M9eScreens.SeedAsync(_host);
        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.Width = 960;
        window.Height = 540;
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = _host.Get<ThemeService>();
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            await M9eScreens.VisitAsync(_host, shell, screen =>
            {
                using var bitmap = new RenderTargetBitmap(new PixelSize(1920, 1080), new Vector(192, 192));
                bitmap.Render(window);
                bitmap.PixelSize.ShouldBe(new PixelSize(1920, 1080));
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                    bitmap.Save(Path.Combine(outputDir, $"{screen}-2x-{theme}.png"));
                }

                return Task.CompletedTask;
            });
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }
}
