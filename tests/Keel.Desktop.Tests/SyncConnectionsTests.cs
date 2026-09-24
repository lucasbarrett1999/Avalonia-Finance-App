using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Sync;
using Keel.Desktop.Views;
using Keel.Desktop.Views.Sync;
using Keel.Domain;

namespace Keel.Desktop.Tests;

/// <summary>Headless flows of Settings → Connections, the add-connection dialogs, health dots and the register's sync state.</summary>
public sealed class SyncConnectionsTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly FakeBankProvider _bank = new();
    private readonly TestHost _host;

    public SyncConnectionsTests() => _host = TestHost.Create(FakeBankProvider.Install(_bank));

    public void Dispose() => _host.Dispose();

    [AvaloniaFact]
    public async Task Connections_section_starts_empty_and_keeps_keys_masked()
    {
        var (window, shell) = Open();
        shell.SettingsItem.NavigateCommand.Execute(null);
        var section = _host.Get<ConnectionsSettingsViewModel>();
        await section.Loading;
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<ConnectionsSettingsView>().Single();

        view.Named<Border>("NoConnections").IsVisible.ShouldBeTrue();
        view.Named<ItemsControl>("ConnectionList").IsVisible.ShouldBeFalse();
        view.Named<TextBlock>("ByoKeysWarning").Text!.ShouldContain("shared production secret");
        section.StoreText.ShouldBe("Memory only (not saved)");
        view.Named<Border>("FallbackWarning").IsVisible.ShouldBeFalse();

        var clientBox = view.Named<TextBox>("ClientIdBox");
        clientBox.IsEffectivelyVisible.ShouldBeTrue();
        clientBox.PasswordChar.ShouldBe('•');
        clientBox.Focus();
        window.Type("my-client-id-123");
        section.ClientIdInput.ShouldBe("my-client-id-123");
        section.SaveClientIdCommand.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => section.HasClientId, "client id saved");
        section.SecretInput = "my-secret-value-456";
        await section.SaveSecretCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var secrets = _host.Get<ISecretStore>();
        (await secrets.GetAsync(SecretKeys.PlaidClientId)).ShouldBe("my-client-id-123");
        (await secrets.GetAsync(SecretKeys.PlaidSecret)).ShouldBe("my-secret-value-456");
        section.ClientIdInput.ShouldBeNull();
        view.Named<TextBox>("ClientIdBox").IsEffectivelyVisible.ShouldBeFalse();
        view.Named<Button>("ReplaceClientIdButton").IsEffectivelyVisible.ShouldBeTrue();

        // Never rendered after entry: no text in the window shows either value.
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)
            .Concat(window.GetVisualDescendants().OfType<TextBox>().Select(t => t.Text ?? string.Empty));
        texts.ShouldNotContain(t => t.Contains("my-client-id-123", StringComparison.Ordinal) || t.Contains("my-secret-value-456", StringComparison.Ordinal));

        // Replace opens an empty editor.
        section.ReplaceClientIdCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        view.Named<TextBox>("ClientIdBox").IsEffectivelyVisible.ShouldBeTrue();
        view.Named<TextBox>("ClientIdBox").Text.ShouldBeNullOrEmpty();
        section.CancelReplaceCommand.Execute(null);

        section.SelectedEnvironment = section.Environments[1];
        await UiTestHelpers.WaitUntilAsync(() => secrets.GetAsync(SecretKeys.PlaidEnvironment).Result == "production", "environment saved");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Add_connection_waits_for_the_browser_maps_accounts_and_syncs()
    {
        await KeysAsync();
        var (window, shell) = Open();
        var browser = new FakeBrowser();
        shell.Sync.Browser = browser;
        shell.SettingsItem.NavigateCommand.Execute(null);
        var section = _host.Get<ConnectionsSettingsViewModel>();
        await section.Loading;
        Dispatcher.UIThread.RunJobs();

        var view = window.GetVisualDescendants().OfType<ConnectionsSettingsView>().Single();
        ImportDialogTests.Click(window, view.Named<Button>("AddConnectionButton"));
        var add = await ImportDialogTests.DialogAsync<AddConnectionViewModel>(shell);
        add.Providers.Single().IsReady.ShouldBeTrue();
        add.CanStart.ShouldBeTrue();
        var dialog = window.GetVisualDescendants().OfType<AddConnectionView>().Single();
        ImportDialogTests.Click(window, dialog.Named<Button>("StartLinkButton"));

        await UiTestHelpers.WaitUntilAsync(() => add.IsWaiting, "waiting for the browser");
        browser.Opened.ShouldHaveSingleItem().Host.ShouldBe("hosted.plaid.com");
        dialog.Named<StackPanel>("WaitingPanel").IsVisible.ShouldBeTrue();
        dialog.Named<Button>("StartLinkButton").IsVisible.ShouldBeFalse();
        add.LinkUrl.ShouldBe("https://hosted.plaid.com/link/test");

        _bank.FinishLink();
        var mapping = await ImportDialogTests.DialogAsync<AccountMappingViewModel>(shell);
        mapping.Rows.Select(r => r.Name).ShouldBe(["Plaid Checking", "Plaid Credit Card"]);
        mapping.Rows[0].SelectedOption.Action.ShouldBe(AccountLinkAction.CreateNew);
        mapping.Rows[1].BalanceText.ShouldBe(LedgerText.Money(-5_510, "USD"));
        mapping.Rows[0].NewName = "Everyday Checking";
        Dispatcher.UIThread.RunJobs();
        var mappingView = window.GetVisualDescendants().OfType<AccountMappingView>().Single();
        ImportDialogTests.Click(window, mappingView.Named<Button>("SaveMappingButton"));

        await shell.Sync.Flow;
        await shell.Sync.Running;
        await UiTestHelpers.WaitUntilAsync(() => section.Connections.Count == 1, "connection listed");
        shell.Status.Message.ShouldBe("Sync finished: 3 added, 0 updated, 0 removed");
        section.Connections[0].InstitutionName.ShouldBe("First Platypus Bank");
        section.Connections[0].AccountsText.ShouldBe("Everyday Checking, Plaid Credit Card");
        section.Connections[0].Health.ShouldBe(HealthKind.Ok);

        // Sidebar: linked accounts show a green health dot with a tooltip; the top bar has the last sync time.
        await shell.AccountsLoading;
        await UiTestHelpers.WaitUntilAsync(() => shell.AccountGroups.SelectMany(g => g.Accounts).Count(a => a.IsLinked) == 2, "sidebar linked");
        var checking = shell.AccountGroups.SelectMany(g => g.Accounts).Single(a => a.Name == "Everyday Checking");
        checking.Health.ShouldBe(HealthKind.Ok);
        checking.HealthToolTip.ShouldBe("Bank connection: Connected");
        checking.AutomationName.ShouldContain("Connected");
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<Ellipse>().Count(e => e.Classes.Contains("health") && e.Classes.Contains("ok") && e.IsEffectivelyVisible).ShouldBeGreaterThanOrEqualTo(2);
        shell.Sync.LastSyncText.ShouldBe("Synced just now");
        shell.SyncAllCommand.CanExecute(null).ShouldBeTrue();

        // The account starts at the bank's balance.
        var account = await _host.Get<IAccountService>().GetAccountAsync(checking.Id, Ct);
        account!.ClearedBalance.Amount.ShouldBe(148_766);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cancelling_while_waiting_stops_the_link()
    {
        await KeysAsync();
        var (window, shell) = Open();
        shell.Sync.Browser = new FakeBrowser(succeeds: false);
        var flow = shell.Sync.AddConnectionAsync();
        var add = await ImportDialogTests.DialogAsync<AddConnectionViewModel>(shell);
        add.ConfirmCommand.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => add.IsWaiting, "waiting");
        add.BrowserFailed.ShouldBeTrue();

        add.CancelCommand.Execute(null);

        (await flow).ShouldBeFalse();
        await UiTestHelpers.WaitUntilAsync(() => _bank.LinkCancelled, "link cancelled");
        shell.Dialogs.Current.ShouldBeNull();
        (await _host.Get<ISyncService>().GetConnectionsAsync(Ct)).ShouldBeEmpty();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Without_keys_the_dialog_says_what_to_do_first()
    {
        var (window, shell) = Open();
        _ = shell.Sync.AddConnectionAsync();
        var add = await ImportDialogTests.DialogAsync<AddConnectionViewModel>(shell);
        add.CanStart.ShouldBeFalse();
        add.ProviderHint.ShouldBe("Enter your Plaid client ID and secret in Settings → Connections first.");
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<AddConnectionView>().Single().Named<Button>("StartLinkButton").IsEnabled.ShouldBeFalse();
        add.CancelCommand.Execute(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_broken_connection_is_a_state_on_the_account_with_a_reconnect_fix()
    {
        await KeysAsync();
        var (window, shell) = Open();
        var connection = await LinkAsync();
        await shell.Sync.RefreshAsync();
        await shell.Sync.SyncAllAsync();
        var checkingId = connection.Accounts.Single(a => a.ProviderAccountId == "acc-checking").AccountId;

        _bank.NeedsReauth = true;
        shell.SyncAllCommand.Execute(null);
        await shell.Sync.Running;
        shell.Status.IsError.ShouldBeTrue();
        shell.Status.Message.ShouldBe("First Platypus Bank: the bank needs you to sign in again (Reconnect)");
        await UiTestHelpers.WaitUntilAsync(() => shell.AccountGroups.SelectMany(g => g.Accounts).Any(a => a.Health == HealthKind.Attention), "amber dot");

        shell.OpenAccount(checkingId);
        var register = _host.Get<AccountsViewModel>();
        await register.SettleAsync();
        register.IsLinked.ShouldBeTrue();
        register.HasConnectionProblem.ShouldBeTrue();
        register.NeedsReconnect.ShouldBeTrue();
        var view = window.Register();
        view.Named<Border>("ConnectionBanner").IsVisible.ShouldBeTrue();
        view.Named<Button>("SyncAccountButton").IsVisible.ShouldBeTrue();

        // The rest of the app keeps working: the register still lists the synced rows.
        register.RowCount.ShouldBe(2);

        ImportDialogTests.Click(window, view.Named<Button>("ReconnectAccountButton"));
        var reconnect = await ImportDialogTests.DialogAsync<AddConnectionViewModel>(shell);
        reconnect.IsReconnect.ShouldBeTrue();
        reconnect.Title.ShouldBe("Reconnect First Platypus Bank");
        reconnect.ConfirmCommand.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => reconnect.IsWaiting, "waiting for sign-in");
        _bank.FinishLink();
        await shell.Sync.Flow;
        await shell.Sync.Running;
        await register.SettleAsync();

        register.HasConnectionProblem.ShouldBeFalse();
        view.Named<Border>("ConnectionBanner").IsVisible.ShouldBeFalse();
        (await _host.Get<ISyncService>().GetConnectionsAsync(Ct)).Single().Status.ShouldBe(SyncStatus.Ok);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Unlink_from_settings_keeps_the_accounts_transactions()
    {
        await KeysAsync();
        var (window, shell) = Open();
        var connection = await LinkAsync();
        await shell.Sync.RefreshAsync();
        await shell.Sync.SyncConnectionAsync(connection.Id);
        shell.SettingsItem.NavigateCommand.Execute(null);
        var section = _host.Get<ConnectionsSettingsViewModel>();
        await section.Loading;
        await UiTestHelpers.WaitUntilAsync(() => section.Connections.Count == 1, "listed");

        section.Connections[0].UnlinkCommand.Execute(null);
        var confirm = await ImportDialogTests.DialogAsync<UnlinkConnectionViewModel>(shell);
        confirm.Message.ShouldContain("Plaid Checking, Plaid Credit Card");
        confirm.ConfirmCommand.Execute(null);
        await shell.Sync.Flow;
        await UiTestHelpers.WaitUntilAsync(() => section.Connections.Count == 0, "unlisted");

        section.HasConnections.ShouldBeFalse();
        _bank.Unlinked.ShouldHaveSingleItem();
        shell.Status.Message.ShouldBe("First Platypus Bank unlinked; its transactions stay in Keel");
        var checking = connection.Accounts.Single(a => a.ProviderAccountId == "acc-checking").AccountId;
        (await _host.Get<IAccountService>().GetAccountAsync(checking, Ct))!.SyncStatus.ShouldBeNull();
        (await _host.Get<Keel.Application.Ledger.IRegisterQuery>().GetSummaryAsync(checking, Ct)).Ledger.ShouldBe(148_766);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sync_schedule_follows_the_setting()
    {
        var (window, shell) = Open();
        shell.SettingsItem.NavigateCommand.Execute(null);
        var section = _host.Get<ConnectionsSettingsViewModel>();
        await section.Loading;
        shell.Sync.ScheduledInterval.ShouldBe(TimeSpan.FromHours(SyncSettings.DefaultIntervalHours));

        section.SelectedInterval = section.Intervals.Single(i => i.Value == 12);
        await UiTestHelpers.WaitUntilAsync(() => shell.Sync.ScheduledInterval == TimeSpan.FromHours(12), "rescheduled");
        (await _host.Get<ISyncService>().GetSettingsAsync(Ct)).IntervalHours.ShouldBe(12);

        section.SelectedInterval = section.Intervals.Single(i => i.Value == 0);
        await UiTestHelpers.WaitUntilAsync(() => shell.Sync.ScheduledInterval is null, "off");
        window.Close();
    }

    private (ShellWindow Window, ShellViewModel Shell) Open()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        return (window, (ShellViewModel)window.DataContext!);
    }

    private async Task KeysAsync()
    {
        var credentials = _host.Get<IBankCredentialsService>();
        await credentials.SetPlaidClientIdAsync("client", Ct);
        await credentials.SetPlaidSecretAsync("secret", Ct);
    }

    // Links through the service (the dialogs are covered above).
    private async Task<SyncConnectionDto> LinkAsync()
    {
        var sync = _host.Get<ISyncService>();
        var session = await sync.BeginLinkAsync("plaid", Ct);
        _bank.FinishLink();
        var pending = await Task.Run(() => sync.CompleteLinkAsync(session, Ct));
        return await Task.Run(() => sync.SaveLinkAsync(
            pending,
            pending.Accounts.Select(a => new AccountLinkChoice(a.Account.ProviderAccountId, AccountLinkAction.CreateNew, NewName: a.Account.Name, NewType: a.Account.SuggestedType)).ToList(),
            Ct));
    }
}
