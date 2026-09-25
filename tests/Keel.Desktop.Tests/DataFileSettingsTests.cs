using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Application.Backup;
using Keel.Application.Files;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Rules;
using Keel.Desktop.ViewModels.Settings;
using Keel.Desktop.Views;
using Keel.Desktop.Views.Settings;
using Keel.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.Tests;

/// <summary>Settings → General (F-SET-1): file location, new/open/move, backups, restore, integrity, diagnostics.</summary>
public sealed class DataFileSettingsTests : IDisposable
{
    private readonly FakeFileDialogs _files = new();
    private readonly TestHost _host;

    public DataFileSettingsTests() => _host = TestHost.Create(services => services.AddSingleton<IFileDialogs>(_files));

    public void Dispose() => _host.Dispose();

    private async Task<(ShellWindow Window, ShellViewModel Shell, DataFileSettingsViewModel Vm, DataFileSettingsView View)> OpenAsync()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        return await SettingsAsync(window, shell);
    }

    private async Task<(ShellWindow Window, ShellViewModel Shell, DataFileSettingsViewModel Vm, DataFileSettingsView View)> SettingsAsync(ShellWindow window, ShellViewModel shell)
    {
        shell.NavigateToSettings("General");
        var vm = _host.Current<DataFileSettingsViewModel>();
        await vm.Loading;
        Dispatcher.UIThread.RunJobs();
        return (window, shell, vm, window.GetVisualDescendants().OfType<DataFileSettingsView>().Single());
    }

    private static async Task<ShellViewModel> SwitchedAsync(ShellWindow window, ShellViewModel previous)
    {
        await UiTestHelpers.WaitUntilAsync(() => !ReferenceEquals(window.DataContext, previous), "the window moved to the new session");
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        Dispatcher.UIThread.RunJobs();
        return shell;
    }

    private Task<AccountDto> AccountAsync(string name) =>
        Task.Run(() => _host.Current<IAccountService>().CreateAccountAsync(new CreateAccountRequest(name, AccountType.Checking, "USD", new DateOnly(2026, 1, 1), 100_00), CancellationToken.None));

    private async Task<List<string>> AccountNamesAsync() =>
        [.. (await Task.Run(() => _host.Current<IAccountService>().GetAccountsAsync(includeClosed: true, CancellationToken.None))).Select(a => a.Name)];

    private static async Task ConfirmAsync(ShellViewModel shell)
    {
        var confirm = await ImportDialogTests.DialogAsync<ConfirmDialogViewModel>(shell);
        confirm.ConfirmCommand.Execute(null);
    }

    [AvaloniaFact]
    public async Task General_shows_the_file_and_backup_now_writes_a_verified_zip_that_restore_brings_back()
    {
        var (window, shell, vm, view) = await OpenAsync();
        var file = _host.Get<AppSession>().BudgetFile!.Path;
        view.Named<TextBlock>("BudgetFilePathText").Text.ShouldBe(file);
        view.Named<TextBlock>("NoBackupsText").IsEffectivelyVisible.ShouldBeTrue();
        vm.AutoBackupEnabled.ShouldBeTrue();

        await AccountAsync("Before backup");
        ImportDialogTests.Click(window, view.Named<Button>("BackupNowButton"));
        await UiTestHelpers.WaitUntilAsync(() => vm.Backups.Count == 1 && !vm.IsBusy, "backup listed");
        var backup = vm.Backups[0].Info;
        backup.Kind.ShouldBe(BackupKind.Manual);
        Path.GetDirectoryName(backup.Path).ShouldBe(_host.DataDirectory.BackupsDirectory);
        backup.FileName.ShouldStartWith("Default-");
        (await Task.Run(() => _host.Get<IBackupService>().VerifyAsync(backup.Path, CancellationToken.None))).ShouldBeTrue();
        shell.StatusMessage.ShouldContain(backup.FileName);

        // Restore asks first; cancelling changes nothing.
        await AccountAsync("After backup");
        var restore = vm.RestoreAsync(vm.Backups[0]);
        var confirm = await ImportDialogTests.DialogAsync<ConfirmDialogViewModel>(shell);
        confirm.CancelCommand.Execute(null);
        await restore;
        (await AccountNamesAsync()).ShouldBe(["After backup", "Before backup"], ignoreOrder: true);

        // Confirmed: the file is replaced and reopened in a new session; a before-restore backup is kept.
        restore = vm.RestoreAsync(vm.Backups[0]);
        await ConfirmAsync(shell);
        var restored = await SwitchedAsync(window, shell);
        await restore;
        (await AccountNamesAsync()).ShouldBe(["Before backup"]);
        _host.Current<AppSession>().BudgetFile!.Path.ShouldBe(file);
        var all = await Task.Run(() => _host.Current<IBackupService>().ListBackupsAsync(CancellationToken.None));
        all.Select(b => b.Kind).ShouldBe([BackupKind.BeforeRestore, BackupKind.Manual], ignoreOrder: true);
        restored.StatusMessage.ShouldNotBeNullOrEmpty();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Open_and_new_switch_to_another_file_and_new_continues_with_the_template_step()
    {
        var (window, shell, _, view) = await OpenAsync();
        await AccountAsync("In the default file");

        // Cancelled pickers do nothing.
        ImportDialogTests.Click(window, view.Named<Button>("OpenFileButton"));
        window.DataContext.ShouldBeSameAs(shell);

        var other = Path.Combine(_host.Root, "elsewhere", "Other.keel");
        _files.SaveBudgetFiles.Enqueue(other);
        ImportDialogTests.Click(window, view.Named<Button>("NewFileButton"));
        var created = await SwitchedAsync(window, shell);
        File.Exists(other).ShouldBeTrue();
        _host.Current<AppSession>().BudgetFile!.Path.ShouldBe(other);
        created.FirstRun.ShouldNotBeNull().IsTemplate.ShouldBeTrue();
        (await AccountNamesAsync()).ShouldBeEmpty();
        created.FirstRun!.Skip();

        // Open the first file again: its data and the remembered file follow.
        (_, _, _, view) = await SettingsAsync(window, created);
        var first = _host.DataDirectory.DefaultBudgetFile;
        _files.OpenBudgetFiles.Enqueue(first);
        ImportDialogTests.Click(window, view.Named<Button>("OpenFileButton"));
        var reopened = await SwitchedAsync(window, created);
        reopened.FileName.ShouldBe("Default.keel");
        (await AccountNamesAsync()).ShouldBe(["In the default file"]);
        _host.Current<IAppSettingsStore>().Current.LastBudgetFile.ShouldBe(first);

        // A file that is not a budget file is refused and the current file stays open.
        var bogus = Path.Combine(_host.Root, "elsewhere", "NotABudget.keel");
        await File.WriteAllTextAsync(bogus, "this is not a SQLite database");
        (_, _, var data, _) = await SettingsAsync(window, reopened);
        _files.OpenBudgetFiles.Enqueue(bogus);
        await data.OpenFileAsync();
        window.DataContext.ShouldBeSameAs(reopened);
        reopened.Status.IsError.ShouldBeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Change_location_moves_the_file_and_its_attachments_after_confirmation()
    {
        var (window, shell, _, view) = await OpenAsync();
        await AccountAsync("Moving along");
        var old = _host.Get<AppSession>().BudgetFile!.Path;
        var oldAttachments = _host.DataDirectory.AttachmentsDirectoryFor(old);
        Directory.CreateDirectory(oldAttachments);
        await File.WriteAllTextAsync(Path.Combine(oldAttachments, "receipt.txt"), "receipt");

        var target = Path.Combine(_host.Root, "synced", "Moved.keel");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        _files.SaveBudgetFiles.Enqueue(target);
        ImportDialogTests.Click(window, view.Named<Button>("MoveFileButton"));
        await ConfirmAsync(shell);
        await UiTestHelpers.WaitUntilAsync(() => !ReferenceEquals(window.DataContext, shell) || shell.Status.IsError, "moved or failed");
        shell.Status.IsError.ShouldBeFalse(shell.StatusMessage);
        var moved = await SwitchedAsync(window, shell);

        _host.Current<AppSession>().BudgetFile!.Path.ShouldBe(target);
        File.Exists(target).ShouldBeTrue();
        File.Exists(old).ShouldBeFalse();
        Directory.Exists(oldAttachments).ShouldBeFalse();
        File.ReadAllText(Path.Combine(_host.DataDirectory.AttachmentsDirectoryFor(target), "receipt.txt")).ShouldBe("receipt");
        (await AccountNamesAsync()).ShouldBe(["Moving along"]);
        _host.Current<IAppSettingsStore>().Current.LastBudgetFile.ShouldBe(target);
        moved.FileName.ShouldBe("Moved.keel");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Integrity_check_and_diagnostic_bundle_report_in_the_status_strip()
    {
        var (window, shell, vm, view) = await OpenAsync();
        await AccountAsync("Secret Payee Account");

        ImportDialogTests.Click(window, view.Named<Button>("CheckIntegrityButton"));
        await UiTestHelpers.WaitUntilAsync(() => vm.IntegrityText is not null && !vm.IsBusy, "integrity checked");
        vm.IntegrityFailed.ShouldBeFalse();
        view.Named<TextBlock>("IntegrityText").IsEffectivelyVisible.ShouldBeTrue();

        ImportDialogTests.Click(window, view.Named<Button>("DiagnosticsButton"));
        await UiTestHelpers.WaitUntilAsync(() => vm.LastDiagnosticBundle is not null && !vm.IsBusy, "bundle written");
        File.Exists(vm.LastDiagnosticBundle).ShouldBeTrue();
        using (var zip = ZipFile.OpenRead(vm.LastDiagnosticBundle!))
        {
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                (await reader.ReadToEndAsync()).ShouldNotContain("Secret Payee Account");
            }
        }

        shell.StatusMessage.ShouldContain(vm.LastDiagnosticBundle!);
        window.Close();
    }
}

/// <summary>The daily jobs: integrity check with a status entry on failure, automatic backups, the update check.</summary>
public sealed class MaintenanceJobsTests : IDisposable
{
    private readonly FakeMaintenance _maintenance = new();
    private readonly FakeUpdateSource _updates = new();
    private readonly TestHost _host;

    public MaintenanceJobsTests() => _host = TestHost.Create(services =>
    {
        services.AddSingleton<IDataFileMaintenance>(_maintenance);
        services.AddSingleton<Func<IUpdateSource>>(() => _updates.Create());
    });

    public void Dispose() => _host.Dispose();

    [AvaloniaFact]
    public async Task A_failed_integrity_check_shows_an_error_and_skips_the_backup_a_healthy_day_backs_up_once()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        var jobs = shell.Maintenance.ShouldNotBeNull();

        _maintenance.Next = new IntegrityCheckResult(false, ["*** in database main ***"], DateTime.UtcNow);
        await jobs.RunAsync();
        shell.Status.IsError.ShouldBeTrue();
        shell.StatusMessage.ShouldBe(Keel.Desktop.Resources.Strings.Maintenance_IntegrityFailed);
        (await Task.Run(() => _host.Get<IBackupService>().ListBackupsAsync(CancellationToken.None))).ShouldBeEmpty();

        _maintenance.Next = new IntegrityCheckResult(true, [], DateTime.UtcNow);
        await jobs.RunAsync();
        await jobs.RunAsync();
        var backups = await Task.Run(() => _host.Get<IBackupService>().ListBackupsAsync(CancellationToken.None));
        backups.ShouldHaveSingleItem().Kind.ShouldBe(BackupKind.Automatic);

        // The update check is off by default: nothing was contacted.
        _host.Get<IAppSettingsStore>().Current.CheckForUpdates.ShouldBeFalse();
        _updates.Created.ShouldBe(0);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_update_check_runs_only_after_opting_in()
    {
        var updates = _host.Get<UpdateService>();
        await updates.CheckAsync(announce: false);
        _updates.Created.ShouldBe(0);
        updates.HasContactedSource.ShouldBeFalse();

        var settings = _host.Get<UpdatesSettingsViewModel>();
        settings.CheckNowCommand.CanExecute(null).ShouldBeFalse();
        settings.CheckForUpdates = true;
        _host.Get<IAppSettingsStore>().Current.CheckForUpdates.ShouldBeTrue();
        await settings.CheckNowCommand.ExecuteAsync(null);
        _updates.Created.ShouldBe(1);
        updates.IsUpdateAvailable.ShouldBeTrue();
        updates.StatusText.ShouldNotBeNull().ShouldContain("9.9.9");

        settings.CheckForUpdates = false;
        updates.IsUpdateAvailable.ShouldBeFalse();
    }

    private sealed class FakeMaintenance : IDataFileMaintenance
    {
        public IntegrityCheckResult Next { get; set; } = new(true, [], DateTime.UtcNow);

        public Task<IntegrityCheckResult> CheckIntegrityAsync(CancellationToken ct) => Task.FromResult(Next);

        public Task<IntegrityCheckResult?> CheckIntegrityIfDueAsync(DateOnly today, CancellationToken ct) => Task.FromResult<IntegrityCheckResult?>(Next);

        public Task CopyToAsync(string destinationPath, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteBudgetFileAsync(string path, CancellationToken ct) => Task.CompletedTask;

        public Task<string> CreateDiagnosticBundleAsync(string destinationDirectory, string appVersion, CancellationToken ct) => Task.FromResult(string.Empty);
    }

    private sealed class FakeUpdateSource : IUpdateSource
    {
        public int Created { get; private set; }

        public bool IsInstalled => true;

        public IUpdateSource Create()
        {
            Created++;
            return this;
        }

        public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>("9.9.9");

        public Task ApplyAndRestartAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
