using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Application.Files;
using Keel.Application.Portability;
using Keel.Application.Settings;
using Keel.Application.Undo;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Budget;
using Keel.Desktop.ViewModels.Import;
using Keel.Desktop.ViewModels.Portability;
using Keel.Desktop.Views;
using Keel.Desktop.Views.FirstRun;
using Keel.Desktop.Views.Portability;
using Keel.Domain;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.Tests;

/// <summary>Answers the export and bundle pickers with queued paths (null = cancelled).</summary>
internal sealed class FakePortabilityDialogs : IPortabilityDialogs
{
    public Queue<string?> Folders { get; } = new();

    public Queue<string?> Zips { get; } = new();

    public Queue<string?> Bundles { get; } = new();

    public Queue<string?> OpenBundles { get; } = new();

    public Task<string?> PickExportFolderAsync(string? startFolder) => Next(Folders);

    public Task<string?> SaveExportZipAsync(string suggestedName, string? startFolder) => Next(Zips);

    public Task<string?> SaveBundleAsync(string suggestedName, string? startFolder) => Next(Bundles);

    public Task<string?> OpenBundleAsync(string? startFolder) => Next(OpenBundles);

    private static Task<string?> Next(Queue<string?> queue) => Task.FromResult(queue.Count > 0 ? queue.Dequeue() : null);
}

/// <summary>Anonymized YNAB and Monarch exports (the Infrastructure fixtures, in short).</summary>
internal static class MigrationSamples
{
    public const string YnabRegister = """
        "Account","Flag","Date","Payee","Category Group/Category","Category Group","Category","Memo","Outflow","Inflow","Cleared"
        "Checking","","01/02/2026","Starting Balance","Inflow: Ready to Assign","Inflow","Ready to Assign","","$0.00","$2,500.00","Reconciled"
        "Checking","","01/03/2026","Corner Grocer","Everyday: Groceries","Everyday","Groceries","weekly shop","$84.12","$0.00","Reconciled"
        "Checking","Red","01/05/2026","City Power","Bills: Electric","Bills","Electric","","$61.40","$0.00","Cleared"
        "Checking","","01/07/2026","Transfer : Savings","","","","","$200.00","$0.00","Cleared"
        "Savings","","01/02/2026","Starting Balance","Inflow: Ready to Assign","Inflow","Ready to Assign","","$0.00","$1,000.00","Reconciled"
        "Savings","","01/07/2026","Transfer : Checking","","","","","$0.00","$200.00","Cleared"
        "Visa Card","","01/10/2026","Bean There Cafe","Everyday: Dining Out","Everyday","Dining Out","","$12.30","$0.00","Uncleared"
        "Checking","","01/16/2026","Acme Payroll","Inflow: Ready to Assign","Inflow","Ready to Assign","","$0.00","$1,850.00","Cleared"
        """;

    public const string YnabBudget = """
        "Month","Category Group/Category","Category Group","Category","Budgeted","Activity","Available"
        "Jan 2026","Everyday: Groceries","Everyday","Groceries","$400.00","-$84.12","$315.88"
        "Jan 2026","Bills: Electric","Bills","Electric","$70.00","-$61.40","$8.60"
        "Feb 2026","Everyday: Groceries","Everyday","Groceries","$450.00","$0.00","$765.88"
        """;

    public const string Monarch = """
        Date,Merchant,Category,Account,Original Statement,Notes,Amount,Tags
        2026-02-01,Acme Payroll,Paychecks,Joint Checking,ACME PAYROLL PPD 0201,,2450.00,
        2026-02-02,Trader Joe's,Groceries,Joint Checking,TRADER JOE S #123,,-86.45,"Household, Weekly"
        2026-02-03,Shell,Gas,Rewards Card,SHELL OIL 5744,,-41.20,
        """;
}

/// <summary>
/// Export, bundle import and the YNAB/Monarch importers through the real window (F-REP-6, PRD 9.10): Settings →
/// General, the first-run Welcome step, and the register's Import file button.
/// </summary>
public sealed class PortabilityTests : IDisposable
{
    private readonly FakePortabilityDialogs _pickers = new();
    private readonly FakeFileDialogs _files = new();
    private readonly string _out = Path.Combine(Path.GetTempPath(), "keel-desktop-tests", "out-" + Guid.NewGuid().ToString("N"));
    private TestHost? _host;

    private TestHost Host => _host ??= TestHost.Create(Register);

    public void Dispose()
    {
        _host?.Dispose();
        try
        {
            Directory.Delete(_out, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Register(IServiceCollection services)
    {
        services.AddSingleton<IPortabilityDialogs>(_pickers);
        services.AddSingleton<IFileDialogs>(_files);
    }

    private async Task<(ShellWindow Window, ShellViewModel Shell)> ShowAsync(TestHost host)
    {
        var window = host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private static async Task<ShellViewModel> SwitchedAsync(ShellWindow window, ShellViewModel previous)
    {
        await UiTestHelpers.WaitUntilAsync(() => !ReferenceEquals(window.DataContext, previous), "the window moved to the new session");
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        Dispatcher.UIThread.RunJobs();
        return shell;
    }

    private static PortabilitySettingsView SettingsSection(ShellWindow window, ShellViewModel shell)
    {
        shell.NavigateToSettings("General");
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<PortabilitySettingsView>().Single();
    }

    private static Task Generate(TestHost host, int count = 400) =>
        Task.Run(() => LedgerFixtureGenerator.GenerateAsync(host.Current<IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(count, Seed: 21, EndDate: DateOnly.FromDateTime(DateTime.Today)), CancellationToken.None));

    private static async Task<List<AccountDto>> AccountsAsync(TestHost host) =>
        [.. await Task.Run(() => host.Current<IAccountService>().GetAccountsAsync(includeClosed: true, CancellationToken.None))];

    [AvaloniaFact]
    public async Task Settings_export_writes_a_csv_zip_a_csv_folder_and_a_bundle()
    {
        await Generate(Host);
        var (window, shell) = await ShowAsync(Host);
        var section = SettingsSection(window, shell);
        var settings = Host.Get<PortabilitySettingsViewModel>();

        // CSV zip (the default).
        var zip = Path.Combine(_out, "export.zip");
        _pickers.Zips.Enqueue(zip);
        ImportDialogTests.Click(window, section.Named<Button>("ExportButton"));
        var dialog = await ImportDialogTests.DialogAsync<ExportDialogViewModel>(shell);
        dialog.IsCsvZip.ShouldBeTrue();
        dialog.ConfirmCommand.Execute(null);
        await settings.Running;
        File.Exists(zip).ShouldBeTrue();
        using (var archive = ZipFile.OpenRead(zip))
        {
            archive.Entries.Select(e => e.FullName).ShouldBe(CsvExportFiles.All);
        }

        shell.StatusMessage.ShouldContain(zip);

        // CSV folder: a new folder inside the chosen one.
        Directory.CreateDirectory(_out);
        _pickers.Folders.Enqueue(_out);
        var export = settings.ExportAsync();
        dialog = await ImportDialogTests.DialogAsync<ExportDialogViewModel>(shell);
        dialog.IsCsvFolder = true;
        dialog.ConfirmText.ShouldBe(Resources.Strings.Export_ConfirmCsv);
        dialog.ConfirmCommand.Execute(null);
        await export;
        var folder = Directory.GetDirectories(_out).ShouldHaveSingleItem();
        Path.GetFileName(folder).ShouldStartWith("Default export ");
        Directory.GetFiles(folder).Select(Path.GetFileName).Order().ShouldBe(CsvExportFiles.All.Order());

        // The bundle; a cancelled picker keeps the dialog open.
        var bundle = Path.Combine(_out, "budget.json");
        _pickers.Bundles.Enqueue(null);
        _pickers.Bundles.Enqueue(bundle);
        export = settings.ExportAsync();
        dialog = await ImportDialogTests.DialogAsync<ExportDialogViewModel>(shell);
        dialog.IsBundle = true;
        dialog.ConfirmText.ShouldBe(Resources.Strings.Export_ConfirmBundle);
        await dialog.ConfirmCommand.ExecuteAsync(null);
        shell.Dialogs.Current.ShouldBe(dialog);
        dialog.ConfirmCommand.Execute(null);
        await export;
        var info = await Task.Run(() => Host.Current<IBundleImportService>().ReadInfoAsync(bundle, CancellationToken.None));
        info.Count(nameof(Keel.Domain.Entities.Transaction)).ShouldBe(400);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_bundle_is_imported_into_a_new_file_that_then_opens()
    {
        await Generate(Host);
        var bundle = Path.Combine(_out, "budget.json");
        await Task.Run(() => Host.Current<IDataExportService>().ExportBundleAsync(bundle, CancellationToken.None));
        var sourceAccounts = await AccountsAsync(Host);
        var (window, shell) = await ShowAsync(Host);
        var section = SettingsSection(window, shell);

        _pickers.OpenBundles.Enqueue(bundle);
        ImportDialogTests.Click(window, section.Named<Button>("ImportBundleButton"));
        var dialog = await ImportDialogTests.DialogAsync<BundleImportDialogViewModel>(shell);
        dialog.CountsText.ShouldContain("400");
        dialog.SourceText.ShouldContain("Default.keel");
        dialog.FileName.ShouldBe("Default (restored)");
        dialog.FileName = "Restored";
        dialog.NewFilePath.ShouldBe(Path.Combine(Host.DataDirectory.BudgetsDirectory, "Restored.keel"));
        dialog.ConfirmCommand.Execute(null);
        var restored = await SwitchedAsync(window, shell);
        Host.Current<AppSession>().BudgetFile!.Path.ShouldBe(Path.Combine(Host.DataDirectory.BudgetsDirectory, "Restored.keel"));
        (await AccountsAsync(Host)).Select(a => (a.Id, a.Name, a.Balance)).ShouldBe(sourceAccounts.Select(a => (a.Id, a.Name, a.Balance)));
        restored.StatusMessage.ShouldContain("Restored.keel");

        // A file that is not a bundle is refused with a clear message.
        var notBundle = Path.Combine(_out, "notes.json");
        await File.WriteAllTextAsync(notBundle, "{\"hello\":1}");
        _pickers.OpenBundles.Enqueue(notBundle);
        await Host.Current<PortabilitySettingsViewModel>().ImportBundleAsync();
        restored.StatusMessage.ShouldBe(Resources.Strings.Bundle_ErrorNotABundle);
        window.Close();
    }

    [AvaloniaFact]
    public async Task First_run_restores_a_keel_export_bundle()
    {
        var bundle = Path.Combine(_out, "household.json");
        using (var source = TestHost.Create())
        {
            await Generate(source, 300);
            await Task.Run(() => source.Current<IDataExportService>().ExportBundleAsync(bundle, CancellationToken.None));
        }

        _host = TestHost.CreateFirstRun(Register);
        var (window, welcomeShell) = await ShowAsync(_host);
        var welcome = welcomeShell.FirstRun.ShouldNotBeNull();
        welcome.CanMigrate.ShouldBeTrue();
        var view = window.GetVisualDescendants().OfType<FirstRunView>().Single();
        view.Named<Border>("PendingBundlePanel").IsEffectivelyVisible.ShouldBeFalse();

        _pickers.OpenBundles.Enqueue(bundle);
        ImportDialogTests.Click(window, view.Named<Button>("RestoreBundleButton"));
        await welcome.Running;
        welcome.HasPendingBundle.ShouldBeTrue();
        welcome.PendingBundleCounts.ShouldContain("300");
        welcome.FileName.ShouldBe("Default (restored)");
        view.Named<Border>("PendingBundlePanel").IsEffectivelyVisible.ShouldBeTrue();

        ImportDialogTests.Click(window, view.Named<Button>("RestoreBundleConfirmButton"));
        var shell = await SwitchedAsync(window, welcomeShell);
        await welcome.Running;
        welcome.Error.ShouldBeNull();
        shell.IsFirstRunVisible.ShouldBeFalse();
        _host.Current<IAppSettingsStore>().Current.FirstRunCompleted.ShouldBeTrue();
        _host.Current<AppSession>().BudgetFile!.Path.ShouldBe(Path.Combine(_host.DataDirectory.BudgetsDirectory, "Default (restored).keel"));
        (await AccountsAsync(_host)).Count.ShouldBe(8);
        window.Close();
    }

    [AvaloniaFact]
    public async Task First_run_creates_a_file_from_a_ynab_export_and_lands_on_budget()
    {
        _host = TestHost.CreateFirstRun(Register);
        var (window, welcomeShell) = await ShowAsync(_host);
        var welcome = welcomeShell.FirstRun.ShouldNotBeNull();
        welcome.ImportPicker = new FakeFilePicker(ImportDialogTests.File("My Budget as of 2026-01-20 - Register.csv", MigrationSamples.YnabRegister));
        welcome.FileName = "From YNAB";
        var view = window.GetVisualDescendants().OfType<FirstRunView>().Single();
        view.Named<Border>("MigrateCard").IsEffectivelyVisible.ShouldBeTrue();

        ImportDialogTests.Click(window, view.Named<Button>("ImportFromAppButton"));
        var shell = await SwitchedAsync(window, welcomeShell);
        _host.Current<AppSession>().BudgetFile!.Path.ShouldBe(Path.Combine(_host.DataDirectory.BudgetsDirectory, "From YNAB.keel"));
        var dialog = await ImportDialogTests.DialogAsync<MigrationDialogViewModel>(shell);
        await dialog.Previewing;
        dialog.Accounts.Select(a => a.SourceAccount).ShouldBe(["Checking", "Savings", "Visa Card"]);
        dialog.Accounts.ShouldAllBe(a => a.IsNew);
        dialog.Accounts[2].TypeNames[dialog.Accounts[2].TypeIndex].ShouldBe(LedgerText.AccountType(AccountType.CreditCard));
        dialog.PreviewText.ShouldContain("8");
        dialog.NewCategoriesText.ShouldContain("Everyday: Groceries");
        dialog.NewTagsText.ShouldContain("Flagged");

        dialog.ConfirmCommand.Execute(null);
        await welcome.Running;
        shell.CurrentPage.ShouldBeOfType<BudgetViewModel>();
        (await AccountsAsync(_host)).Select(a => a.Name).ShouldBe(["Checking", "Savings", "Visa Card"], ignoreOrder: true);
        shell.StatusMessage.ShouldContain("3 accounts created");
        _host.Current<IUndoService>().NextUndo.ShouldBe(LedgerAction.ImportTransactions);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_register_import_button_hands_a_monarch_export_to_the_migration_preview()
    {
        var checking = await Task.Run(() => Host.Get<IAccountService>().CreateAccountAsync(new CreateAccountRequest("Joint Checking", AccountType.Checking, "USD", new DateOnly(2026, 1, 1), 0), CancellationToken.None));
        Host.Get<ImportWorkflow>().FilePicker = new FakeFilePicker(ImportDialogTests.File("monarch.csv", MigrationSamples.Monarch));
        var (window, shell) = await ShowAsync(Host);
        shell.OpenAccount(checking.Id);
        await Host.Get<AccountsViewModel>().SettleAsync();

        ImportDialogTests.Click(window, window.Register().Named<Button>("ImportFileButton"));
        var dialog = await ImportDialogTests.DialogAsync<MigrationDialogViewModel>(shell);
        await dialog.Previewing;
        dialog.Title.ShouldContain(Resources.Strings.Monarch_Name);
        dialog.Accounts[0].Target!.AccountId.ShouldBe(checking.Id, "matched by name");
        dialog.Accounts[1].IsNew.ShouldBeTrue();

        // Leaving the card out updates the dry run.
        dialog.Accounts[1].Target = dialog.Accounts[1].Targets.Single(t => t.IsSkip);
        await dialog.Previewing;
        dialog.Accounts[1].ResultText.ShouldBe(Resources.Strings.Ynab_AccountSkipped);
        dialog.PreviewText.ShouldStartWith("2 new");
        dialog.ConfirmCommand.Execute(null);
        await Host.Get<ImportWorkflow>().Running;
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is null, "dialog closed");
        (await AccountsAsync(Host)).ShouldHaveSingleItem().Balance.Amount.ShouldBe(2_450_00 - 86_45);
        shell.Status.CanUndo.ShouldBeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_ynab_budget_export_writes_assigned_amounts()
    {
        var (window, shell) = await ShowAsync(Host);
        var section = SettingsSection(window, shell);
        var settings = Host.Get<PortabilitySettingsViewModel>();
        settings.Migration.FilePicker = new FakeFilePicker(ImportDialogTests.File("Plan.csv", MigrationSamples.YnabBudget));

        ImportDialogTests.Click(window, section.Named<Button>("ImportFromAppButton"));
        var dialog = await ImportDialogTests.DialogAsync<BudgetImportDialogViewModel>(shell);
        dialog.CanImport.ShouldBeTrue();
        dialog.Preview.Assignments.ShouldBe(3);
        dialog.PreviewText.ShouldContain("2 months");
        dialog.ConfirmCommand.Execute(null);
        await settings.Running;
        shell.StatusMessage.ShouldContain("3 assigned amounts");
        await using var db = Host.Current<IDbContextFactory<KeelDbContext>>().CreateDbContext();
        (await db.BudgetAssignments.SumAsync(b => b.Assigned)).ShouldBe(400_00 + 70_00 + 450_00);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_file_that_is_not_an_export_is_refused_and_the_palette_lists_the_commands()
    {
        var (window, shell) = await ShowAsync(Host);
        var settings = Host.Get<PortabilitySettingsViewModel>();
        settings.Migration.FilePicker = new FakeFilePicker(ImportDialogTests.File("bank.csv", ImportDialogTests.BankCsv));
        await settings.ImportFromAppAsync();
        shell.StatusMessage.ShouldContain("bank.csv");
        shell.Dialogs.Current.ShouldBeNull();

        var ids = Host.Get<AppCommands>().Build(shell).Select(c => c.Id).ToList();
        ids.ShouldContain("export-data");
        ids.ShouldContain("import-bundle");
        ids.ShouldContain("import-ynab-monarch");
        window.Close();
    }
}
