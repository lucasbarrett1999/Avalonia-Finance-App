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
    public async Task Export_bundle_and_migration_dialogs_render_at_2x_on_a_1080p_display()
    {
        await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(_host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(300, Seed: 6, EndDate: DateOnly.FromDateTime(DateTime.Today)), CancellationToken.None));
        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.Width = 960;
        window.Height = 540;
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = _host.Get<ThemeService>();
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");
        var scenes = await PortabilityScenes.DialogsAsync(_host, shell, Path.Combine(_host.Root, "exports"));
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            foreach (var (name, open) in scenes)
            {
                var showing = open();
                await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is not null, name + " shown");
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
                    // The 1x frame at 960 × 540; a 2x bitmap of the dialog layer scales dialog content twice in the
                    // headless renderer (existing dialogs too), so only the narrow frame is saved for review.
                    using var frame = window.CaptureRenderedFrame()!;
                    frame.Save(Path.Combine(outputDir, $"{name}-narrow-{theme}.png"));
                }

                shell.Dialogs.Current!.CancelCommand.Execute(null);
                await showing;
            }
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task Tags_attachments_and_payee_merge_render_at_2x_on_a_1080p_display()
    {
        var ledger = await TagTestLedger.CreateAsync(_host);
        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.Width = 960;
        window.Height = 540;
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = _host.Get<ThemeService>();
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");

        async Task RenderAsync(string name)
        {
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
                bitmap.Save(Path.Combine(outputDir, $"M9c-{name}-2x-{themes.Current}.png"));
            }
        }

        async Task DialogAsync(string name, Func<Task> open)
        {
            var opening = open();
            await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is not null, name + " shown");
            if (shell.Dialogs.Current is Keel.Desktop.ViewModels.Rules.MergePayeesDialogViewModel merge)
            {
                await merge.Refreshing;
            }

            await RenderAsync(name);
            shell.Dialogs.Current!.CancelCommand.Execute(null);
            await opening;
        }

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            shell.OpenAccount(ledger.Checking);
            var register = _host.Get<AccountsViewModel>();
            await register.SettleAsync();
            await UiTestHelpers.WaitUntilAsync(() => register.Rows.LoadedRows.Any(r => r.Id == ledger.Hotel), "rows loaded");
            window.Register().Grid.SelectedItem = register.Rows.LoadedRows.First(r => r.Id == ledger.Hotel);
            Dispatcher.UIThread.RunJobs();
            await register.EditTagsAsync();
            await UiTestHelpers.WaitUntilAsync(() => register.Editor?.Attachments?.Items.Count == 1, "attachments listed");
            await RenderAsync("Editor");
            register.CancelEdit();

            shell.NavigateToSettings("Tags");
            var tags = _host.Get<Keel.Desktop.ViewModels.Settings.TagsSettingsViewModel>();
            await UiTestHelpers.WaitUntilAsync(() => tags.Tags.Count == 3, "tags loaded");
            await RenderAsync("SettingsTags");
            var trip = tags.Tags.Single(t => t.Name == "Trip 2026");
            await DialogAsync("RenameTag", () => tags.RenameCommand.ExecuteAsync(trip));
            await DialogAsync("MergeTag", () => tags.MergeCommand.ExecuteAsync(trip));
            await DialogAsync("DeleteTag", () => tags.DeleteCommand.ExecuteAsync(trip));

            var payees = _host.Get<Keel.Desktop.ViewModels.Rules.PayeesViewModel>();
            shell.NavigateToSettings("Payees");
            await payees.EnsureLoadedAsync();
            await payees.Loading;
            foreach (var row in payees.Payees.Where(p => p.Name.StartsWith("am", StringComparison.OrdinalIgnoreCase)))
            {
                row.IsSelected = true;
            }

            await DialogAsync("MergePayees", () => payees.MergeCommand.ExecuteAsync(null));
            payees.ClearSelectionCommand.Execute(null);
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }
}
