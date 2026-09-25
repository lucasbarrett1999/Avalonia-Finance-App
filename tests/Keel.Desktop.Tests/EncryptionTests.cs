using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Application.Files;
using Keel.Application.Sync;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Settings;
using Keel.Desktop.Views;
using Keel.Domain;
using Keel.Infrastructure.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.Tests;

/// <summary>F-SET-4 in the app: encrypt and decrypt from Settings, the passphrase prompt, the remembered key (ADR 0101).</summary>
public sealed class EncryptionTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private readonly InMemorySecretStore _secrets = new();
    private readonly FakeFileDialogs _files = new();
    private TestHost _host;

    public EncryptionTests() => _host = TestHost.Create(Configure);

    public void Dispose() => _host.Dispose();

    private void Configure(IServiceCollection services)
    {
        // One secret store for every session and app run, like the OS store.
        services.AddSingleton(_secrets);
        services.AddSingleton<IFileDialogs>(_files);
    }

    // A new app run over the same data directory; the previous run ends first, as it would (single instance).
    private TestHost Restart()
    {
        _host.KeepFiles = true;
        _host.Dispose();
        _host = TestHost.Reopen(_host.Root, Configure);
        return _host;
    }

    private string FilePath => _host.DataDirectory.DefaultBudgetFile;

    private async Task<(ShellWindow Window, ShellViewModel Shell)> ShowAsync()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private static async Task<ShellViewModel> SwitchedAsync(ShellWindow window, ShellViewModel previous)
    {
        await UiTestHelpers.WaitUntilAsync(() => !ReferenceEquals(window.DataContext, previous), "the window moved to the new session", 30_000);
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        Dispatcher.UIThread.RunJobs();
        return shell;
    }

    private Task<AccountDto> AccountAsync(string name) =>
        Task.Run(() => _host.Current<IAccountService>().CreateAccountAsync(new CreateAccountRequest(name, AccountType.Checking, "USD", new DateOnly(2026, 1, 1), 100_00), CancellationToken.None));

    private async Task<List<string>> AccountNamesAsync() =>
        [.. (await Task.Run(() => _host.Current<IAccountService>().GetAccountsAsync(includeClosed: true, CancellationToken.None))).Select(a => a.Name)];

    private async Task<EncryptionSettingsViewModel> EncryptionSettingsAsync(ShellViewModel shell)
    {
        shell.NavigateToSettings("General");
        var vm = _host.Current<EncryptionSettingsViewModel>();
        await vm.Loading;
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    // Whether the key of the budget file (by its SQLCipher salt) is in the secret store.
    private bool KeyRemembered(string path)
    {
        var salt = new byte[16];
        using (var stream = File.OpenRead(path))
        {
            stream.ReadExactly(salt);
        }

        return _secrets.Contains(SecretKeys.BudgetFile(Convert.ToHexStringLower(salt)));
    }

    private static bool IsPlain(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[16];
        stream.ReadExactly(header);
        return System.Text.Encoding.ASCII.GetString(header) == "SQLite format 3\0";
    }

    private async Task<ShellViewModel> EncryptAsync(ShellWindow window, ShellViewModel shell, bool remember)
    {
        var settings = await EncryptionSettingsAsync(shell);
        var encrypting = settings.EncryptAsync();
        var dialog = await ImportDialogTests.DialogAsync<EncryptFileViewModel>(shell);
        dialog.Passphrase = Passphrase;
        dialog.ConfirmPassphrase = Passphrase;
        dialog.Acknowledged = true;
        dialog.Remember = remember;
        dialog.ConfirmCommand.Execute(null);
        var encrypted = await SwitchedAsync(window, shell);
        await encrypting;
        return encrypted;
    }

    [AvaloniaFact]
    public async Task Encrypting_from_settings_validates_the_passphrase_converts_the_file_and_keeps_working()
    {
        await AccountAsync("Everyday");
        var (window, shell) = await ShowAsync();
        var settings = await EncryptionSettingsAsync(shell);
        settings.IsEncrypted.ShouldBeFalse();
        settings.StatusText.ShouldBe(Keel.Desktop.Resources.Strings.Encrypt_StatusPlain);
        var view = window.GetVisualDescendants().OfType<Keel.Desktop.Views.Settings.EncryptionSettingsView>().Single();
        view.Named<Button>("EncryptButton").IsEffectivelyVisible.ShouldBeTrue();
        view.Named<Button>("RemoveEncryptionButton").IsVisible.ShouldBeFalse();

        ImportDialogTests.Click(window, view.Named<Button>("EncryptButton"));
        var dialog = await ImportDialogTests.DialogAsync<EncryptFileViewModel>(shell);
        var dialogView = window.GetVisualDescendants().OfType<Keel.Desktop.Views.Dialogs.EncryptFileView>().Single();
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() == dialogView.Named<TextBox>("PassphraseBox"), "passphrase box focused");
        dialogView.Named<TextBox>("PassphraseBox").PasswordChar.ShouldBe('•');

        dialog.Passphrase = "short";
        dialog.ConfirmCommand.Execute(null);
        dialog.Error.ShouldBe(Keel.Desktop.Resources.Strings.Encrypt_TooShort);
        dialog.Passphrase = Passphrase;
        dialog.ConfirmPassphrase = Passphrase + "!";
        dialog.ConfirmCommand.Execute(null);
        dialog.Error.ShouldBe(Keel.Desktop.Resources.Strings.Encrypt_Mismatch);
        dialog.ConfirmPassphrase = Passphrase;
        dialog.ConfirmCommand.Execute(null);
        dialog.Error.ShouldBe(Keel.Desktop.Resources.Strings.Encrypt_AcknowledgeRequired);
        IsPlain(FilePath).ShouldBeTrue("nothing happens until the dialog is valid");

        dialog.Acknowledged = true;
        dialog.ConfirmCommand.Execute(null);
        var encrypted = await SwitchedAsync(window, shell);

        IsPlain(FilePath).ShouldBeFalse();
        _host.Current<AppSession>().BudgetFile!.IsEncrypted.ShouldBeTrue();
        encrypted.StatusMessage.ShouldContain("Default.keel");
        encrypted.Status.IsError.ShouldBeFalse();
        Directory.EnumerateFiles(_host.DataDirectory.BackupsDirectory, "Default-*-before-encryption.zip").ShouldHaveSingleItem();
        (await AccountNamesAsync()).ShouldBe(["Everyday"]);
        await AccountAsync("Added after encryption");
        (await AccountNamesAsync()).Count.ShouldBe(2);
        KeyRemembered(FilePath).ShouldBeFalse("the key is remembered only on request");

        var after = await EncryptionSettingsAsync(encrypted);
        after.IsEncrypted.ShouldBeTrue();
        after.CanRemove.ShouldBeTrue();
        after.IsKeyRemembered.ShouldBeFalse();
        after.StoreText.ShouldNotBeNull();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_new_app_run_asks_for_the_passphrase_refuses_a_wrong_one_and_can_remember_the_key()
    {
        await AccountAsync("Everyday");
        var (window, shell) = await ShowAsync();
        await EncryptAsync(window, shell, remember: false);
        window.Close();

        // A new app run: the key ring is empty and nothing is remembered, so the shell asks.
        Restart();
        _host.Current<AppSession>().LockedFile.ShouldBe(FilePath);
        _host.Current<AppSession>().BudgetFile.ShouldBeNull();
        (window, shell) = await ShowAsync();
        var prompt = await ImportDialogTests.DialogAsync<UnlockFileViewModel>(shell);
        prompt.Message.ShouldContain("Default.keel");
        var promptView = window.GetVisualDescendants().OfType<Keel.Desktop.Views.Dialogs.UnlockFileView>().Single();
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() == promptView.Named<TextBox>("PassphraseBox"), "passphrase box focused");

        window.Type("wrong passphrase");
        window.Press(PhysicalKey.Enter);
        await UiTestHelpers.WaitUntilAsync(() => prompt.HasError && !prompt.IsBusy, "wrong passphrase refused");
        prompt.Error.ShouldBe(Keel.Desktop.Resources.Strings.Encrypt_WrongPassphrase);
        shell.Dialogs.Current.ShouldBeSameAs(prompt, "the prompt stays open");

        prompt.Passphrase = Passphrase;
        prompt.Remember = true;
        prompt.ConfirmCommand.Execute(null);
        var unlocked = await SwitchedAsync(window, shell);
        unlocked.LockedFile.ShouldBeNull();
        (await AccountNamesAsync()).ShouldBe(["Everyday"]);
        KeyRemembered(FilePath).ShouldBeTrue();
        (await _secrets.GetAsync(SecretKeys.BudgetFile(Convert.ToHexStringLower(File.ReadAllBytes(FilePath).AsSpan(0, 16)))))!.ShouldNotContain(Passphrase);
        window.Close();

        // Remembered: the next run opens it without asking; Settings can forget the key again.
        Restart();
        _host.Current<AppSession>().BudgetFile!.IsEncrypted.ShouldBeTrue();
        (window, shell) = await ShowAsync();
        shell.Dialogs.Current.ShouldBeNull();
        var settings = await EncryptionSettingsAsync(shell);
        settings.IsKeyRemembered.ShouldBeTrue();
        settings.IsKeyRemembered = false;
        await UiTestHelpers.WaitUntilAsync(() => shell.StatusMessage == Keel.Desktop.Resources.Strings.Encrypt_ForgottenStatus, "key forgotten");
        KeyRemembered(FilePath).ShouldBeFalse();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cancelling_the_prompt_leaves_the_file_locked_and_the_palette_offers_unlock()
    {
        var (window, shell) = await ShowAsync();
        await EncryptAsync(window, shell, remember: false);
        window.Close();

        Restart();
        (window, shell) = await ShowAsync();
        var prompt = await ImportDialogTests.DialogAsync<UnlockFileViewModel>(shell);
        prompt.CancelCommand.Execute(null);
        await shell.UnlockPrompt;
        shell.StatusMessage.ShouldContain("still locked");
        shell.FileName.ShouldBe(Keel.Desktop.Resources.Strings.Shell_NoFile);

        var commands = _host.Current<AppCommands>().Build(shell);
        commands.Single(c => c.Id == "unlock-file").IsEnabled.ShouldBeTrue();
        commands.Single(c => c.Id == "encrypt-file").IsEnabled.ShouldBeFalse();
        commands.Single(c => c.Id == "backup").IsEnabled.ShouldBeFalse();

        // Settings → General shows the locked state with an Unlock button.
        var settings = await EncryptionSettingsAsync(shell);
        settings.IsLocked.ShouldBeTrue();
        settings.StatusText.ShouldBe(Keel.Desktop.Resources.Strings.Encrypt_StatusLocked);

        commands.Single(c => c.Id == "unlock-file").Execute();
        prompt = await ImportDialogTests.DialogAsync<UnlockFileViewModel>(shell);
        prompt.Passphrase = Passphrase;
        prompt.ConfirmCommand.Execute(null);
        await SwitchedAsync(window, shell);
        _host.Current<AppSession>().BudgetFile!.IsEncrypted.ShouldBeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Removing_encryption_checks_the_passphrase_and_writes_a_plain_file()
    {
        await AccountAsync("Everyday");
        var (window, shell) = await ShowAsync();
        shell = await EncryptAsync(window, shell, remember: true);
        KeyRemembered(FilePath).ShouldBeTrue();
        var fileId = Convert.ToHexStringLower(File.ReadAllBytes(FilePath).AsSpan(0, 16));

        var commands = _host.Current<AppCommands>().Build(shell);
        commands.Single(c => c.Id == "encrypt-file").IsEnabled.ShouldBeFalse();
        commands.Single(c => c.Id == "remove-encryption").IsEnabled.ShouldBeTrue();
        commands.Single(c => c.Id == "unlock-file").IsEnabled.ShouldBeFalse();

        var settings = await EncryptionSettingsAsync(shell);
        var removing = settings.RemoveEncryptionAsync();
        var dialog = await ImportDialogTests.DialogAsync<RemoveEncryptionViewModel>(shell);
        dialog.Passphrase = "not it";
        dialog.ConfirmCommand.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => dialog.HasError && !dialog.IsBusy, "wrong passphrase");
        dialog.Error.ShouldBe(Keel.Desktop.Resources.Strings.Encrypt_WrongPassphrase);
        IsPlain(FilePath).ShouldBeFalse();

        dialog.Passphrase = Passphrase;
        dialog.ConfirmCommand.Execute(null);
        var plain = await SwitchedAsync(window, shell);
        await removing;

        IsPlain(FilePath).ShouldBeTrue();
        _secrets.Contains(SecretKeys.BudgetFile(fileId)).ShouldBeFalse("removing the encryption forgets the key");
        _host.Current<AppSession>().BudgetFile!.IsEncrypted.ShouldBeFalse();
        plain.StatusMessage.ShouldContain("plain SQLite file");
        Directory.EnumerateFiles(_host.DataDirectory.BackupsDirectory, "Default-*-before-decryption.zip").ShouldHaveSingleItem();
        (await AccountNamesAsync()).ShouldBe(["Everyday"]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Opening_another_encrypted_file_asks_in_the_current_window_and_cancel_keeps_the_current_file()
    {
        // Make an encrypted file elsewhere, then return to a fresh plain file.
        var (window, shell) = await ShowAsync();
        await AccountAsync("In the encrypted file");
        shell = await EncryptAsync(window, shell, remember: false);
        var encryptedPath = FilePath;
        var other = Path.Combine(_host.DataDirectory.BudgetsDirectory, "Other.keel");
        await _host.Sessions.OpenAsync(other);
        shell = await SwitchedAsync(window, shell);
        _host.Sessions.KeyRing.Forget(Convert.ToHexStringLower(File.ReadAllBytes(encryptedPath).AsSpan(0, 16)));

        var data = _host.Current<DataFileSettingsViewModel>();
        _files.OpenBudgetFiles.Enqueue(encryptedPath);
        var opening = data.OpenFileAsync();
        var prompt = await ImportDialogTests.DialogAsync<UnlockFileViewModel>(shell);
        prompt.CancelCommand.Execute(null);
        await opening;
        ReferenceEquals(window.DataContext, shell).ShouldBeTrue("cancelling keeps the current file open");
        _host.Current<AppSession>().BudgetFile!.FileName.ShouldBe("Other.keel");

        _files.OpenBudgetFiles.Enqueue(encryptedPath);
        opening = data.OpenFileAsync();
        prompt = await ImportDialogTests.DialogAsync<UnlockFileViewModel>(shell);
        prompt.Passphrase = Passphrase;
        prompt.ConfirmCommand.Execute(null);
        await SwitchedAsync(window, shell);
        await opening;
        (await AccountNamesAsync()).ShouldBe(["In the encrypted file"]);
        window.Close();
    }

    [Fact]
    public void Passphrase_dialogs_validate_without_a_window()
    {
        var encrypt = new EncryptFileViewModel("Home.keel", new Keel.Application.Security.SecretStoreDescription(Keel.Application.Security.SecretStoreBackend.EncryptedFile, IsWeakerFallback: true), (_, _) => Task.CompletedTask);
        encrypt.IsWeakerStore.ShouldBeTrue();
        encrypt.StoreText!.ShouldContain("fallback");
        encrypt.Intro.ShouldContain("Home.keel");
        encrypt.Validate().ShouldBe(Keel.Desktop.Resources.Strings.Encrypt_TooShort);
        encrypt.Passphrase = encrypt.ConfirmPassphrase = "eight ch";
        encrypt.Acknowledged = true;
        encrypt.Validate().ShouldBeNull();
        new UnlockFileViewModel("/x/Home.keel", (_, _) => Task.CompletedTask).ToString()!.ShouldNotContain("eight");
    }
}
