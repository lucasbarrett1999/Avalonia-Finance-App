using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Rules;
using Keel.Desktop.ViewModels.Settings;
using Keel.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.Tests;

/// <summary>
/// Renders the M9c screens (F-TXN-8, F-TXN-9) in light and dark without binding or resource errors: the register
/// with tag chips and paperclips, the editor with tags and attachments, Settings → Tags and Payees, and the rename,
/// merge and delete dialogs. With KEEL_SCREENSHOT_DIR set the frames are saved for review.
/// </summary>
public sealed class M9cRenderingTests
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
    public async Task Tags_attachments_and_payee_merge_render_in_light_and_dark()
    {
        LogCapture.Instance.Clear();
        var files = new FakeAttachmentFiles();
        using var host = TestHost.Create(services => services.AddSingleton<IAttachmentFiles>(files));
        var ledger = await TagTestLedger.CreateAsync(host);
        var window = host.Get<ShellWindow>();
        window.Width = 1280;
        window.Height = 800;
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = host.Get<ThemeService>();

        shell.OpenAccount(ledger.Checking);
        var register = host.Get<AccountsViewModel>();
        await register.SettleAsync();
        await UiTestHelpers.WaitUntilAsync(() => register.Rows.LoadedRows.Any(r => r.Id == ledger.Hotel), "rows loaded");
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            await CaptureAsync(window, $"M9c-Register-{theme}");
        }

        var view = window.Register();
        view.Grid.SelectedItem = register.Rows.LoadedRows.First(r => r.Id == ledger.Hotel);
        Dispatcher.UIThread.RunJobs();
        await register.EditTagsAsync();
        await register.Editor!.Attachments!.AddFilesAsync([ledger.File("hotel-folio.png", "png")]);
        register.Editor.Tags.Text = "tr";
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            await CaptureAsync(window, $"M9c-Editor-{theme}");
        }

        register.CancelEdit();
        shell.NavigateToSettings("Tags");
        var tags = host.Get<TagsSettingsViewModel>();
        await UiTestHelpers.WaitUntilAsync(() => tags.Tags.Count == 3, "tags loaded");
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            await CaptureAsync(window, $"M9c-SettingsTags-{theme}");
        }

        async Task DialogAsync(string name, Func<Task> open)
        {
            var opening = open();
            await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is not null, name + " shown");
            if (shell.Dialogs.Current is MergePayeesDialogViewModel merge)
            {
                await merge.Refreshing;
            }

            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                themes.SetTheme(theme);
                await CaptureAsync(window, $"M9c-{name}-{theme}");
            }

            shell.Dialogs.Current!.CancelCommand.Execute(null);
            await opening;
        }

        var trip = tags.Tags.Single(t => t.Name == "Trip 2026");
        await DialogAsync("RenameTag", () => tags.RenameCommand.ExecuteAsync(trip));
        await DialogAsync("MergeTag", () => tags.MergeCommand.ExecuteAsync(trip));
        await DialogAsync("DeleteTag", () => tags.DeleteCommand.ExecuteAsync(trip));

        shell.NavigateToSettings("Payees");
        var payees = host.Get<PayeesViewModel>();
        await payees.EnsureLoadedAsync();
        await payees.Loading;
        foreach (var row in payees.Payees.Where(p => p.Name.StartsWith("am", StringComparison.OrdinalIgnoreCase)))
        {
            row.IsSelected = true;
        }

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            await CaptureAsync(window, $"M9c-SettingsPayees-{theme}");
        }

        await DialogAsync("MergePayees", () => payees.MergeCommand.ExecuteAsync(null));

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }
}
