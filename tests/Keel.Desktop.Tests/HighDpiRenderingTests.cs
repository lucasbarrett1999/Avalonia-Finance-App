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
    public async Task Debt_payoff_budget_health_and_debt_terms_render_at_2x_on_a_1080p_display()
    {
        var fixture = await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(_host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(1_000, Seed: 3, EndDate: DateOnly.FromDateTime(DateTime.Today)), CancellationToken.None));
        await DebtHealthRenderingTests.AddDebtTermsAsync(_host, fixture);
        LogCapture.Instance.Clear();
        var window = _host.Get<ShellWindow>();
        window.Width = 960;
        window.Height = 540;
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var goals = _host.Get<GoalsViewModel>();
        var reports = _host.Get<ReportsViewModel>();
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");

        async Task RenderAsync(string name)
        {
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }

            using var bitmap = new RenderTargetBitmap(new PixelSize(1920, 1080), new Vector(192, 192));
            bitmap.Render(window);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                bitmap.Save(Path.Combine(outputDir, name + ".png"));
            }
        }

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            _host.Get<ThemeService>().SetTheme(theme);
            shell.NavigateTo<GoalsViewModel>();
            goals.SelectedTab = GoalsTab.DebtPayoff;
            await UiTestHelpers.WaitUntilAsync(() => goals.DebtPayoff!.IsInitialized, "debt plan loaded");
            await RenderAsync($"DebtPayoff-2x-{theme}");

            var editing = goals.DebtPayoff!.EditAccountCommand.ExecuteAsync(goals.DebtPayoff.Missing[0]);
            await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is not null, "editor shown");
            await RenderAsync($"AccountEditorDebt-2x-{theme}");
            shell.Dialogs.Current!.CancelCommand.Execute(null);
            await editing;
            goals.SelectedTab = GoalsTab.Goals;

            shell.NavigateTo<ReportsViewModel>();
            reports.SelectedReport = reports.Reports.Single(r => r.Kind == Keel.Desktop.ViewModels.Reports.ReportKind.BudgetHealth);
            await UiTestHelpers.WaitUntilAsync(() => reports.Loading.IsCompleted, "health loaded");
            await RenderAsync($"BudgetHealth-2x-{theme}");
        }

        _host.Get<ThemeService>().SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }
}
