using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Application.Import;
using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Import;
using Keel.Desktop.Views;
using Keel.Desktop.Views.Import;
using Keel.Domain;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Desktop.Tests;

/// <summary>A file picker that returns a prepared file (or nothing) and records what it was asked.</summary>
internal sealed class FakeFilePicker(params PickedImportFile?[] files) : IImportFilePicker
{
    private readonly Queue<PickedImportFile?> _files = new(files);

    public List<(string Title, string? Folder)> Calls { get; } = [];

    public Task<PickedImportFile?> PickAsync(string title, string? startFolder)
    {
        Calls.Add((title, startFolder));
        return Task.FromResult(_files.Count > 0 ? _files.Dequeue() : null);
    }
}

/// <summary>Headless tests of the import flow: register and sidebar entry points, mapping and preview dialogs.</summary>
public sealed class ImportDialogTests : IDisposable
{
    internal const string BankCsv = """
        Date,Description,Amount
        2026-08-03,SQ *BLUE BOTTLE COFFEE,-5.75
        2026-08-04,PAYROLL ACME CORP,2500.00
        2026-08-05,TRADER JOE'S #552,-42.50
        """;

    internal const string AmbiguousCsv = """
        Posted,Description,Debit,Credit
        03/04/2026,CORNER STORE,12.00,
        05/06/2026,REFUND,,4.00
        """;

    private static readonly DateOnly Opening = new(2026, 8, 1);
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    internal static PickedImportFile File(string name, string text, string? folder = "/home/me/Downloads") =>
        new(name, folder, Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n")));

    [AvaloniaFact]
    public async Task Register_import_button_maps_previews_imports_and_undoes()
    {
        var checking = await AccountAsync("Checking");
        var picker = new FakeFilePicker(File("august.csv", BankCsv));
        _host.Get<ImportWorkflow>().FilePicker = picker;
        var (window, shell, vm) = await OpenAsync(checking.Id);
        vm.RowCount.ShouldBe(0);
        vm.ShowEmptyState.ShouldBeTrue();

        Click(window, window.Register().Named<Button>("ImportFileButton"));
        var mapping = await DialogAsync<CsvMappingViewModel>(shell);
        picker.Calls.ShouldHaveSingleItem().Title.ShouldContain("Checking");
        await mapping.Refreshing;
        Dispatcher.UIThread.RunJobs();
        var mappingView = window.GetVisualDescendants().OfType<CsvMappingView>().Single();
        mappingView.Named<ComboBox>("DateColumnBox").SelectedItem.ShouldBe(mapping.Columns[1]);
        mappingView.Named<ComboBox>("AmountColumnBox").SelectedItem.ShouldBe(mapping.Columns[3]);
        mapping.IsRemembered.ShouldBeFalse();
        mapping.PreviewRows.Count.ShouldBe(3);
        mappingView.Named<ItemsControl>("PreviewRowsList").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("PAYROLL ACME CORP");

        Click(window, mappingView.Named<Button>("ContinueButton"));
        var preview = await DialogAsync<ImportPreviewViewModel>(shell);
        Dispatcher.UIThread.RunJobs();
        preview.Rows.Select(r => r.StatusText).ShouldBe(["New", "New", "New"]);
        preview.Rows.Select(r => r.Payee).ShouldBe(["Blue Bottle Coffee", "Payroll Acme Corp", "Trader Joes"]);
        preview.ImportButtonText.ShouldBe("Import 3");

        // Leave the coffee out with its checkbox; the totals follow.
        var previewView = window.GetVisualDescendants().OfType<ImportPreviewView>().Single();
        var grid = previewView.Named<DataGrid>("PreviewGrid");
        await UiTestHelpers.WaitUntilAsync(() => grid.GetVisualDescendants().OfType<CheckBox>().Any(c => AutomationProperties.GetName(c) == "Import row 1"), "rows realized");
        grid.GetVisualDescendants().OfType<CheckBox>().Single(c => AutomationProperties.GetName(c) == "Import row 1").IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        preview.ImportButtonText.ShouldBe("Import 2");
        previewView.Named<TextBlock>("TotalsText").Text.ShouldStartWith("Importing 2 of 3 rows. New: 2 ·");

        Click(window, previewView.Named<Button>("ImportButton"));
        var summary = await _host.Get<ImportWorkflow>().Running;
        summary.ShouldNotBeNull();
        (summary.Added, summary.SkippedByChoice).ShouldBe((2, 1));
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is null && vm.RowCount == 2, "imported rows in the register");
        await vm.SettleAsync();
        shell.StatusMessage.ShouldBe("Imported august.csv into Checking: 2 new, 1 left out, 2 uncategorized.");
        shell.Status.CanUndo.ShouldBeTrue();
        _host.Get<IUndoService>().NextUndo.ShouldBe(LedgerAction.ImportTransactions);
        (await _host.Get<IImportSettingsStore>().GetLastFolderAsync(checking.Id, Ct)).ShouldBe("/home/me/Downloads");

        // Undo from the toast removes the whole import.
        Click(window, window.Named<Button>("StatusUndoButton"));
        await UiTestHelpers.WaitUntilAsync(() => vm.RowCount == 0, "import undone");
        shell.StatusMessage.ShouldBe("Undid import transactions.");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Mapping_dialog_asks_about_ambiguous_dates_and_previews_live()
    {
        var checking = await AccountAsync("Checking");
        _host.Get<ImportWorkflow>().FilePicker = new FakeFilePicker(File("export.csv", AmbiguousCsv));
        var (window, shell, vm) = await OpenAsync(checking.Id);

        var run = vm.ImportFileCommand.ExecuteAsync(null);
        var mapping = await DialogAsync<CsvMappingViewModel>(shell);
        await mapping.Refreshing;
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<CsvMappingView>().Single();
        mapping.IsDateAmbiguous.ShouldBeTrue();
        view.Named<Border>("AmbiguousDatesBar").IsVisible.ShouldBeTrue();
        mapping.IsDebitCredit.ShouldBeTrue();
        mapping.PayeeColumn!.Index.ShouldBe(1);
        view.Named<ComboBox>("DebitColumnBox").IsEffectivelyVisible.ShouldBeTrue();
        view.Named<ComboBox>("AmountColumnBox").IsEffectivelyVisible.ShouldBeFalse();
        mapping.PreviewRows[0].Date.ShouldBe(new DateOnly(2026, 3, 4).ToString("d", System.Globalization.CultureInfo.CurrentCulture));

        // Continuing without answering is refused.
        Click(window, view.Named<Button>("ContinueButton"));
        await UiTestHelpers.WaitUntilAsync(() => mapping.HasError, "asks for the date order");
        mapping.Error.ShouldBe("Choose month-first or day-first dates before continuing.");

        Click(window, view.Named<Button>("DayFirstButton"));
        await mapping.Refreshing;
        Dispatcher.UIThread.RunJobs();
        mapping.SelectedDateFormat.ShouldBe("dd/MM/yyyy");
        view.Named<Border>("AmbiguousDatesBar").IsVisible.ShouldBeFalse();
        mapping.PreviewRows[0].Date.ShouldBe(new DateOnly(2026, 4, 3).ToString("d", System.Globalization.CultureInfo.CurrentCulture));
        mapping.PreviewRows.Select(r => r.IsNegative).ShouldBe([true, false]);

        // Switching the layout to a single amount column without one shows why nothing can be read.
        view.Named<ComboBox>("AmountLayoutBox").SelectedIndex = 0;
        await mapping.Refreshing;
        Dispatcher.UIThread.RunJobs();
        mapping.HasPreviewRows.ShouldBeFalse();
        view.Named<TextBlock>("PreviewMessageText").Text.ShouldBe("Choose the amount columns.");
        view.Named<ComboBox>("AmountLayoutBox").SelectedIndex = 1;
        await mapping.Refreshing;
        mapping.ParsedCount.ShouldBe(2);

        Click(window, view.Named<Button>("ContinueButton"));
        var preview = await DialogAsync<ImportPreviewViewModel>(shell);
        preview.Rows.Select(r => r.Amount).ShouldBe([-1_200L, 400L]);
        preview.CancelCommand.Execute(null);
        await run;
        (await _host.Get<ImportWorkflow>().Running).ShouldBeNull();
        (await ImportedAsync(checking.Id)).ShouldBeEmpty("cancelled imports write nothing");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Mapping_is_remembered_per_account()
    {
        var checking = await AccountAsync("Checking");
        var workflow = _host.Get<ImportWorkflow>();
        workflow.FilePicker = new FakeFilePicker(File("card.csv", BankCsv), File("card.csv", BankCsv));
        var (window, shell, _) = await OpenAsync(checking.Id);

        var run = workflow.ImportAsync(checking.Id);
        var mapping = await DialogAsync<CsvMappingViewModel>(shell);
        mapping.SelectedSignConvention = mapping.SignConventions.Single(c => c.Value == CsvSignConvention.OutflowPositive);
        mapping.PayeeColumn = mapping.Columns[0];
        await mapping.Refreshing;
        mapping.PreviewRows[0].Payee.ShouldBeEmpty();
        mapping.PreviewRows[0].IsNegative.ShouldBeFalse("outflow-positive flips the sign");
        mapping.ConfirmCommand.Execute(null);
        var preview = await DialogAsync<ImportPreviewViewModel>(shell);
        preview.CancelCommand.Execute(null);
        await run;

        var second = workflow.ImportAsync(checking.Id);
        var again = await DialogAsync<CsvMappingViewModel>(shell);
        again.IsRemembered.ShouldBeTrue();
        again.SelectedSignConvention.Value.ShouldBe(CsvSignConvention.OutflowPositive);
        again.PayeeColumn!.Index.ShouldBeNull();
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<CsvMappingView>().Single().Named<TextBlock>("MappingSourceText").Text
            .ShouldBe("Using the mapping you chose for this account last time.");
        again.CancelCommand.Execute(null);
        (await second).ShouldBeNull();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Preview_lists_every_dedup_status_and_imports_the_choices()
    {
        var checking = await AccountAsync("Checking");
        var savings = await AccountAsync("Savings", AccountType.Savings);
        await Task.Run(() => _host.Get<ITransactionService>().SaveAsync(new SaveTransactionRequest(null, checking.Id, new DateOnly(2026, 8, 9), -4_250, "Trader Joe's", null, null), Ct));
        await Task.Run(() => _host.Get<IImportService>().ImportTransactionsAsync(TransactionSource.File, new ImportBatch(checking.Id,
        [
            new(new DateOnly(2026, 8, 2), -1_000, "OLD COFFEE", ProviderTransactionId: "A1"),
            new(new DateOnly(2026, 8, 3), -2_000, "GAS", ProviderTransactionId: "A2"),
        ]), Ct));
        await Task.Run(() => _host.Get<IImportService>().ImportTransactionsAsync(TransactionSource.File, new ImportBatch(savings.Id,
            [new(new DateOnly(2026, 8, 6), 30_000, "FROM CHECKING")]), Ct));

        var ofx = Ofx(("A1", "20260802", "-10.00", "OLD COFFEE"), ("A2", "20260803", "-21.00", "GAS"), ("A3", "20260810", "-42.50", "TRADER JOE'S #552"),
            ("A4", "20260805", "-300.00", "TRANSFER TO SAVINGS"), ("A5", "20260811", "-9.99", "NEW CAFE"));
        _host.Get<ImportWorkflow>().FilePicker = new FakeFilePicker(new PickedImportFile("statement.ofx", null, Encoding.UTF8.GetBytes(ofx)));
        var (window, shell, vm) = await OpenAsync(checking.Id);

        var run = _host.Get<ImportWorkflow>().ImportAsync(checking.Id);
        var preview = await DialogAsync<ImportPreviewViewModel>(shell);
        Dispatcher.UIThread.RunJobs();
        preview.Rows.Select(r => r.StatusText).ShouldBe(["Duplicate", "Updated", "Matched to existing", "Transfer pair", "New"]);
        preview.Rows.Select(r => r.Include).ShouldBe([false, true, true, true, true]);
        preview.Rows[0].CanOverride.ShouldBeFalse("an unchanged FITID match has nothing to apply");
        preview.Rows[3].TransferText.ShouldBe("Transfer: Savings");
        preview.HasReportedBalance.ShouldBeTrue();
        window.GetVisualDescendants().OfType<ImportPreviewView>().Single().Named<TextBlock>("TotalsText").Text
            .ShouldBe("Importing 4 of 5 rows. New: 2 · Matched: 1 · Updated: 1 · Transfer pairs: 1 · Duplicates left out: 1");

        // Un-pair the transfer: it stays a new row, not a transfer.
        preview.Rows[3].PairTransfer = false;
        preview.Rows[3].StatusText.ShouldBe("New");
        preview.ConfirmCommand.Execute(null);
        var summary = (await run)!;
        (summary.Added, summary.Updated, summary.MatchedToExisting, summary.TransfersMatched, summary.DuplicatesSkipped).ShouldBe((2, 1, 1, 0, 1));
        shell.StatusMessage.ShouldContain("reported balance $1,234.56");
        await UiTestHelpers.WaitUntilAsync(() => vm.RowCount == 5, "register refreshed");
        await UiTestHelpers.WaitUntilAsync(() => vm.HasReported && vm.ReportedText == "$1,234.56", "reported balance in the header");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sidebar_menu_and_all_accounts_start_an_import()
    {
        var checking = await AccountAsync("Checking");
        var card = await AccountAsync("Visa", AccountType.CreditCard);
        var picker = new FakeFilePicker(null, File("x.csv", BankCsv));
        _host.Get<ImportWorkflow>().FilePicker = picker;
        var (window, shell, vm) = await OpenAsync(null);

        // Sidebar context menu entry: opens the account and asks for a file (cancelled here).
        var item = shell.AccountGroups.SelectMany(g => g.Accounts).Single(a => a.Id == card.Id);
        await item.ImportFileCommand.ExecuteAsync(null);
        shell.CurrentPage.ShouldBeSameAs(vm);
        vm.AccountId.ShouldBe(card.Id);
        picker.Calls.ShouldHaveSingleItem().Title.ShouldBe("Import transactions into Visa");
        shell.Dialogs.Current.ShouldBeNull();
        var menu = window.GetVisualDescendants().OfType<RadioButton>().First(r => r.DataContext == item).ContextMenu!;
        menu.Items.OfType<MenuItem>().Select(m => m.Header).ShouldContain("Import file…");

        // All Accounts: the account is chosen first.
        shell.AccountItems[0].NavigateCommand.Execute(null);
        await vm.SettleAsync();
        var run = vm.ImportFileCommand.ExecuteAsync(null);
        var chooser = await DialogAsync<PickerDialogViewModel>(shell);
        chooser.Items.Select(i => i.Label).ShouldBe(["Checking", "Visa"]);
        chooser.Selected = chooser.Items[0];
        chooser.ConfirmCommand.Execute(null);
        var mapping = await DialogAsync<CsvMappingViewModel>(shell);
        picker.Calls[1].Title.ShouldBe("Import transactions into Checking");
        mapping.CancelCommand.Execute(null);
        await run;
        window.Close();
        _ = checking;
    }

    internal static string Ofx(params (string Fitid, string Date, string Amount, string Name)[] rows) => $"""
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102

        <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD
        <BANKACCTFROM><BANKID>123<ACCTID>999<ACCTTYPE>CHECKING</BANKACCTFROM>
        <BANKTRANLIST><DTSTART>20260801<DTEND>20260831
        {string.Concat(rows.Select(r => $"<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>{r.Date}<TRNAMT>{r.Amount}<FITID>{r.Fitid}<NAME>{r.Name}</STMTTRN>\n"))}</BANKTRANLIST>
        <LEDGERBAL><BALAMT>1234.56<DTASOF>20260831</LEDGERBAL>
        </STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
        """;

    internal static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    internal static async Task<T> DialogAsync<T>(ShellViewModel shell)
        where T : DialogViewModel
    {
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is T, typeof(T).Name + " shown");
        Dispatcher.UIThread.RunJobs();
        return (T)shell.Dialogs.Current!;
    }

    private async Task<AccountDto> AccountAsync(string name, AccountType type = AccountType.Checking) =>
        await Task.Run(() => _host.Get<IAccountService>().CreateAccountAsync(new CreateAccountRequest(name, type, "USD", Opening, 0), Ct));

    private async Task<List<Keel.Domain.Entities.Transaction>> ImportedAsync(Guid accountId)
    {
        await using var db = _host.Get<IDbContextFactory<KeelDbContext>>().CreateDbContext();
        return await db.Transactions.Where(t => t.AccountId == accountId && t.Source == TransactionSource.File).ToListAsync();
    }

    private async Task<(ShellWindow Window, ShellViewModel Shell, AccountsViewModel Vm)> OpenAsync(Guid? accountId)
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        if (accountId is { } id)
        {
            shell.OpenAccount(id);
        }
        else
        {
            shell.AccountItems[0].NavigateCommand.Execute(null);
        }

        var vm = _host.Get<AccountsViewModel>();
        await vm.SettleAsync();
        return (window, shell, vm);
    }
}
