using Avalonia.Threading;
using Keel.Application.Accounts;
using Keel.Application.Budget;
using Keel.Application.Categories;
using Keel.Application.Settings;
using Keel.Application.Stats;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Settings;
using Keel.Domain;

namespace Keel.Desktop.Tests;

/// <summary>
/// Walks the M9 stream E screens (Settings → Privacy &amp; Stats, the Encryption section, the encryption dialogs and the
/// palette with unavailable commands) and calls <c>visit</c> on each, for the accessibility, rendering and 2x tests.
/// </summary>
internal static class M9eScreens
{
    /// <summary>Gives the file data for every stats card and one measured cold start and scroll session.</summary>
    public static async Task SeedAsync(TestHost host)
    {
        var account = await Task.Run(() => host.Get<IAccountService>().CreateAccountAsync(new CreateAccountRequest("Everyday", AccountType.Checking, "USD", new DateOnly(2026, 1, 1), 1_250_00), CancellationToken.None));
        var category = await Task.Run(() => host.Get<ICategoryService>().CreateCategoryAsync("Everyday", "Groceries", CancellationToken.None));
        await Task.Run(() => host.Get<IBudgetService>().AssignAsync(category.Id, new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1), 200_00, CancellationToken.None));
        host.Get<IAppSettingsStore>().Update(s => s with
        {
            Stats = s.Stats with
            {
                ColdStart = new ColdStartSample(1_380, 100_000, Encrypted: false, DateTime.UtcNow.AddHours(-2)),
                RegisterScroll = new RegisterScrollSample(100_000, 412, 9.8, 15.1, DateTime.UtcNow.AddHours(-1)),
            },
        });
        _ = account;
    }

    public static async Task SettleAsync()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Visits each screen once in the current theme.</summary>
    public static async Task VisitAsync(TestHost host, ShellViewModel shell, Func<string, Task> visit)
    {
        shell.NavigateToSettings("Privacy");
        await host.Current<StatsSettingsViewModel>().Loading;
        await SettleAsync();
        await visit("Settings-Privacy");

        shell.NavigateToSettings("Encryption");
        await host.Current<EncryptionSettingsViewModel>().Loading;
        await host.Current<DataFileSettingsViewModel>().Loading;
        await SettleAsync();
        await visit("Settings-Encryption");

        await DialogAsync(shell, "EncryptFile", () => host.Current<EncryptionSettingsViewModel>().EncryptAsync(), dialog =>
        {
            var encrypt = (EncryptFileViewModel)dialog;
            encrypt.Passphrase = "short";
            encrypt.ConfirmCommand.Execute(null); // shows the validation error
        }, visit);

        await DialogAsync(shell, "UnlockFile", () => shell.Dialogs.ShowAsync(new UnlockFileViewModel(host.DataDirectory.DefaultBudgetFile, (_, _) => throw new Keel.Application.Files.BudgetFileLockedException(host.DataDirectory.DefaultBudgetFile, wrongPassphrase: true))), dialog =>
        {
            var unlock = (UnlockFileViewModel)dialog;
            unlock.Passphrase = "not the passphrase";
            unlock.ConfirmCommand.Execute(null);
        }, visit);

        await DialogAsync(shell, "RemoveEncryption", () => shell.Dialogs.ShowAsync(new RemoveEncryptionViewModel("Default.keel", _ => Task.FromResult(false), _ => Task.CompletedTask)), _ => { }, visit);

        var palette = shell.OpenCommandPaletteAsync();
        await UiTestHelpers.WaitUntilAsync(() => shell.Palette is not null, "palette");
        shell.Palette!.Query = "reconcile";
        await SettleAsync();
        await visit("CommandPalette-Unavailable");
        shell.Dialogs.Current!.CancelCommand.Execute(null);
        await palette;
    }

    private static async Task DialogAsync(ShellViewModel shell, string name, Func<Task> open, Action<DialogViewModel> prepare, Func<string, Task> visit)
    {
        var opening = open();
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is not null, name + " shown");
        prepare(shell.Dialogs.Current!);
        await UiTestHelpers.WaitUntilAsync(() => !shell.Dialogs.Current!.IsBusy, name + " settled");
        await SettleAsync();
        await visit(name);
        shell.Dialogs.Current!.CancelCommand.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is null, name + " closed");
        await opening;
    }
}
