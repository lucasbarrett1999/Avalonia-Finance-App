using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Settings;
using Keel.Application.Sync;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Sync;
using Keel.Desktop.Views;
using Keel.Desktop.Views.Sync;

namespace Keel.Desktop.Tests;

/// <summary>Renders Settings → Connections and the add-connection dialogs in light and dark (PNG with KEEL_SCREENSHOT_DIR).</summary>
public sealed class SyncRenderingTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly FakeBankProvider _bank = new();
    private readonly TestHost _host;

    public SyncRenderingTests() => _host = TestHost.Create(FakeBankProvider.Install(_bank));

    public void Dispose() => _host.Dispose();

    [AvaloniaFact]
    public async Task Connections_section_and_dialogs_render_in_light_and_dark()
    {
        var outputDir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");
        var themes = _host.Get<ThemeService>();
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        var section = _host.Get<ConnectionsSettingsViewModel>();
        await section.Loading;
        LogCapture.Instance.Clear();

        // Empty state, before any keys.
        var standalone = new Window { Width = 900, Height = 1250, Content = new ConnectionsSettingsView { DataContext = section, Margin = new Avalonia.Thickness(24) } };
        standalone.Show();
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            Save(standalone, outputDir, $"Connections-Empty-{theme}.png");
        }

        // Two connections, one needing a new sign-in.
        var credentials = _host.Get<IBankCredentialsService>();
        await credentials.SetPlaidClientIdAsync("client", Ct);
        await credentials.SetPlaidSecretAsync("secret", Ct);
        var sync = _host.Get<ISyncService>();
        var first = await LinkAsync(sync);
        await Task.Run(() => sync.SyncConnectionAsync(first.Id, null, Ct));
        var second = await LinkAsync(sync);
        _bank.NeedsReauth = true;
        await Task.Run(() => sync.SyncConnectionAsync(second.Id, null, Ct));
        await section.LoadAsync();
        await shell.Sync.RefreshAsync();
        await shell.AccountsLoading;
        section.Connections.Count.ShouldBe(2);

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);
            Save(standalone, outputDir, $"Connections-{theme}.png");
        }

        standalone.Close();

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);

            // Sidebar dots and the register banner of the broken connection.
            shell.OpenAccount(second.Accounts[0].AccountId);
            await _host.Get<AccountsViewModel>().SettleAsync();
            Save(window, outputDir, $"Register-Reconnect-{theme}.png");

            // Waiting for the browser.
            _bank.NeedsReauth = false;
            shell.Sync.Browser = new FakeBrowser();
            var flow = shell.Sync.AddConnectionAsync();
            var add = await ImportDialogTests.DialogAsync<AddConnectionViewModel>(shell);
            Save(window, outputDir, $"AddConnection-Choose-{theme}.png");
            add.ConfirmCommand.Execute(null);
            await UiTestHelpers.WaitUntilAsync(() => add.IsWaiting, "waiting");
            Save(window, outputDir, $"AddConnection-Waiting-{theme}.png");

            // Mapping.
            _bank.FinishLink();
            var mapping = await ImportDialogTests.DialogAsync<AccountMappingViewModel>(shell);
            Dispatcher.UIThread.RunJobs();
            mapping.Rows[1].SelectedOption = mapping.Rows[1].Options[^1];
            await Task.Delay(50);
            Save(window, outputDir, $"AccountMapping-{theme}.png");
            mapping.Rows[1].IsCreatingNew.ShouldBeFalse();
            window.GetVisualDescendants().OfType<AccountMappingView>().Single().GetVisualDescendants().OfType<ComboBox>()
                .Count(c => c.SelectedItem is MappingOption { Action: AccountLinkAction.Skip }).ShouldBe(1);
            mapping.CancelCommand.Execute(null);
            (await flow).ShouldBeFalse();
            _bank.NeedsReauth = true;
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }

    private async Task<SyncConnectionDto> LinkAsync(ISyncService sync)
    {
        var session = await sync.BeginLinkAsync("plaid", Ct);
        _bank.FinishLink();
        var pending = await Task.Run(() => sync.CompleteLinkAsync(session, Ct));
        return await Task.Run(() => sync.SaveLinkAsync(
            pending,
            pending.Accounts.Select(a => new AccountLinkChoice(a.Account.ProviderAccountId, AccountLinkAction.CreateNew, NewName: a.Account.Name, NewType: a.Account.SuggestedType)).ToList(),
            Ct));
    }

    private static void Save(Window window, string? outputDir, string name)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        using var frame = window.CaptureRenderedFrame()!;
        frame.PixelSize.Width.ShouldBeGreaterThan(0);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
            frame.Save(Path.Combine(outputDir, name));
        }
    }
}
