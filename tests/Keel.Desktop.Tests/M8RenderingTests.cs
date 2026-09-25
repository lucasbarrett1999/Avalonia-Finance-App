using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
/// Renders the M8 screens in light and dark without binding or resource errors: the first-run steps, the
/// Home checklist, Settings (General, Appearance, Bills, Keyboard, Updates), the command palette and About.
/// With KEEL_SCREENSHOT_DIR set the frames are saved (the README screenshots come from here and the
/// other rendering tests).
/// </summary>
public sealed class M8RenderingTests
{
    private static async Task CaptureAsync(Window window, string name)
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }

        using var frame = window.CaptureRenderedFrame()!;
        frame.PixelSize.Width.ShouldBeGreaterThan(0);
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
            frame.Save(Path.Combine(outputDir, name + ".png"));
        }
    }

    [AvaloniaFact]
    public async Task First_run_steps_render_in_light_and_dark()
    {
        LogCapture.Instance.Clear();
        using var host = TestHost.CreateFirstRun();
        var window = host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        var themes = host.Get<ThemeService>();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            await CaptureAsync(window, $"FirstRun-Welcome-{theme}");
        }

        await shell.FirstRun!.CreateAsync();
        await UiTestHelpers.WaitUntilAsync(() => !ReferenceEquals(window.DataContext, shell), "switched");
        var setup = ((ShellViewModel)window.DataContext!).FirstRun!;
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            await CaptureAsync(window, $"FirstRun-Template-{theme}");
        }

        await setup.ApplyTemplateAsync();
        setup.Balance = 2_450_00;
        setup.ToggleBankInfo();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            await CaptureAsync(window, $"FirstRun-Account-{theme}");
        }

        await setup.AddAccountAsync();
        var home = host.Current<HomeViewModel>();
        ((ShellViewModel)window.DataContext!).NavigateTo<HomeViewModel>();
        await UiTestHelpers.WaitUntilAsync(() => home.IsInitialized, "home loaded");
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            await CaptureAsync(window, $"Home-Checklist-{theme}");
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task Settings_sections_palette_and_about_render_in_light_and_dark()
    {
        LogCapture.Instance.Clear();
        using var host = TestHost.Create();
        await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(600, Seed: 9, EndDate: DateOnly.FromDateTime(DateTime.Today)), CancellationToken.None));
        await Task.Run(() => host.Get<Keel.Application.Backup.IBackupService>().BackupNowAsync(CancellationToken.None));
        var window = host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = host.Get<ThemeService>();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            foreach (var section in new[] { "General", "Appearance", "Bills", "Keyboard", "Updates" })
            {
                shell.NavigateToSettings(section);
                await host.Get<Keel.Desktop.ViewModels.Settings.DataFileSettingsViewModel>().Loading;
                await host.Get<Keel.Desktop.ViewModels.Settings.BillsSettingsViewModel>().Loading;
                await CaptureAsync(window, $"Settings-{section}-{theme}");
            }

            var palette = shell.OpenCommandPaletteAsync();
            await UiTestHelpers.WaitUntilAsync(() => shell.Palette is not null, "palette");
            shell.Palette!.Query = "back";
            await CaptureAsync(window, $"CommandPalette-{theme}");
            shell.Dialogs.Current!.CancelCommand.Execute(null);
            await palette;

            host.Get<AppCommands>().Build(shell).Single(c => c.Id == "about").Execute();
            await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is not null, "about");
            await CaptureAsync(window, $"About-{theme}");
            shell.Dialogs.Current!.CancelCommand.Execute(null);
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }
}
