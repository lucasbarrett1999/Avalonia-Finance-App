using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Application.Files;
using Keel.Application.Import;
using Keel.Application.Portability;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Import;
using Keel.Desktop.ViewModels.Portability;
using Keel.Desktop.Views;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.Tests;

/// <summary>The export, bundle and migration dialogs, built from real data, for rendering and audits.</summary>
internal static class PortabilityScenes
{
    /// <summary>Each dialog with a name; opening one shows it in <paramref name="shell"/>'s dialog layer and completes when it closes.</summary>
    public static async Task<IReadOnlyList<(string Name, Func<Task> Open)>> DialogsAsync(TestHost host, ShellViewModel shell, string folder)
    {
        var services = host.CurrentServices;
        var bundle = Path.Combine(folder, "budget.json");
        Directory.CreateDirectory(folder);
        var info = await Task.Run(() => services.GetRequiredService<IDataExportService>().ExportBundleAsync(bundle, CancellationToken.None));
        var migration = services.GetRequiredService<IMigrationImportService>();
        var accounts = await Task.Run(() => services.GetRequiredService<IAccountService>().GetAccountsAsync(false, CancellationToken.None));
        var register = await ParseAsync(services, "Register.csv", MigrationSamples.YnabRegister);
        var plan = await Task.Run(() => migration.PlanAsync(register, CancellationToken.None));
        var budget = await ParseAsync(services, "Plan.csv", MigrationSamples.YnabBudget);
        var budgetPreview = await Task.Run(() => migration.PreviewBudgetAsync(budget, CancellationToken.None));
        var dialogs = services.GetRequiredService<DialogService>();
        return
        [
            ("ExportDialog", () => dialogs.ShowAsync(new ExportDialogViewModel(services.GetRequiredService<IDataExportService>(), services.GetRequiredService<IPortabilityDialogs>(), "Household.keel", null))),
            ("BundleImportDialog", () => dialogs.ShowAsync(new BundleImportDialogViewModel(info, services.GetRequiredService<IBundleImportService>(), services.GetRequiredService<IFileDialogs>(), folder))),
            ("MigrationDialog", async () =>
            {
                var dialog = new MigrationDialogViewModel("My Budget - Register.csv", register, plan, accounts, "USD", migration);
                var showing = dialogs.ShowAsync(dialog);
                await dialog.Previewing;
                await showing;
            }),
            ("BudgetImportDialog", () => dialogs.ShowAsync(new BudgetImportDialogViewModel("My Budget - Plan.csv", budget, budgetPreview, migration))),
        ];
    }

    private static async Task<ParseResult> ParseAsync(IServiceProvider services, string name, string text)
    {
        var file = ImportDialogTests.File(name, text);
        return (await MigrationWorkflow.ParseAsync(services.GetRequiredService<IFileImportParserResolver>(), file, "USD"))!;
    }
}

/// <summary>
/// Renders the export and import screens of M9 stream D in light and dark without binding or resource errors:
/// Settings → General with the export section, the export, bundle, migration and budget dialogs, and the
/// first-run Welcome step with a chosen bundle. With KEEL_SCREENSHOT_DIR set the frames are saved.
/// </summary>
public sealed class PortabilityRenderingTests
{
    private static async Task CaptureAsync(Window window, string name)
    {
        for (var i = 0; i < 6; i++)
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
    public async Task Export_and_import_screens_render_in_light_and_dark()
    {
        LogCapture.Instance.Clear();
        using var host = TestHost.Create();
        await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(host.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(600, Seed: 9, EndDate: DateOnly.FromDateTime(DateTime.Today)), CancellationToken.None));
        var window = host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = host.Get<ThemeService>();
        var scenes = await PortabilityScenes.DialogsAsync(host, shell, Path.Combine(host.Root, "exports"));
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            shell.NavigateToSettings("General");
            await host.Get<Keel.Desktop.ViewModels.Settings.DataFileSettingsViewModel>().Loading;
            await UiTestHelpers.WaitUntilAsync(() => window.GetVisualDescendants().OfType<Keel.Desktop.Views.Portability.PortabilitySettingsView>().Any(), "settings shown");
            var section = window.GetVisualDescendants().OfType<Keel.Desktop.Views.Portability.PortabilitySettingsView>().Single();
            section.BringIntoView();
            await CaptureAsync(window, $"Settings-Export-{theme}");
            foreach (var (name, open) in scenes)
            {
                var showing = open();
                await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is not null, name + " shown");
                if (shell.Dialogs.Current is MigrationDialogViewModel migration)
                {
                    await migration.Previewing;
                }

                await CaptureAsync(window, $"{name}-{theme}");
                shell.Dialogs.Current!.CancelCommand.Execute(null);
                await showing;
            }
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task First_run_welcome_with_a_chosen_bundle_renders_in_light_and_dark()
    {
        LogCapture.Instance.Clear();
        var bundle = Path.Combine(Path.GetTempPath(), "keel-desktop-tests", "bundle-" + Guid.NewGuid().ToString("N") + ".json");
        using (var source = TestHost.Create())
        {
            await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(source.Get<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(200, Seed: 4), CancellationToken.None));
            await Task.Run(() => source.Get<IDataExportService>().ExportBundleAsync(bundle, CancellationToken.None));
        }

        var pickers = new FakePortabilityDialogs();
        pickers.OpenBundles.Enqueue(bundle);
        using var host = TestHost.CreateFirstRun(services => services.AddSingleton<IPortabilityDialogs>(pickers));
        var window = host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        var themes = host.Get<ThemeService>();
        await shell.FirstRun!.ChooseBundleAsync();
        shell.FirstRun.HasPendingBundle.ShouldBeTrue();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            await CaptureAsync(window, $"FirstRun-Welcome-Bundle-{theme}");
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        File.Delete(bundle);
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }
}
