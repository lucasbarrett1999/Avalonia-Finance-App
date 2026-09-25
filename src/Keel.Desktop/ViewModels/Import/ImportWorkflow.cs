using System.Globalization;
using Keel.Application.Accounts;
using Keel.Application.Categories;
using Keel.Application.Import;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Import;

/// <summary>
/// The file import flow (F-TXN-2) behind the register's "Import file" button and the sidebar
/// account menu: account chooser (All Accounts), file picker, CSV column mapping (remembered per
/// account), preview with per-row overrides, import through <see cref="IImportService"/>, and the
/// result toast with Undo in the status strip.
/// </summary>
public sealed class ImportWorkflow(
    IImportService imports,
    IFileImportParserResolver parsers,
    IImportSettingsStore settings,
    IAccountService accounts,
    ICategoryService categories,
    DialogService dialogs,
    StatusService status,
    IImportFilePicker picker,
    Portability.MigrationWorkflow? migration = null)
{
    /// <summary>Bytes the resolver looks at to recognize a format.</summary>
    private const int HeadLength = 4096;

    /// <summary>The file picker (tests replace it).</summary>
    public IImportFilePicker FilePicker { get; set; } = picker;

    /// <summary>The latest run (tests await it).</summary>
    public Task<ImportSummary?> Running { get; private set; } = Task.FromResult<ImportSummary?>(null);

    /// <summary>
    /// Runs the whole flow for <paramref name="accountId"/>, or asks for the account first when null
    /// (All Accounts). Returns the summary, or null when cancelled or failed (the status strip says why).
    /// </summary>
    public Task<ImportSummary?> ImportAsync(Guid? accountId)
    {
        Running = RunAsync(accountId);
        return Running;
    }

    /// <summary>Imports an already-read file into an account (mapping, preview, import, toast).</summary>
    public async Task<ImportSummary?> ImportFileAsync(AccountDto account, PickedImportFile file)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(file);
        var parser = parsers.Resolve(file.Name, file.Bytes.AsSpan(0, Math.Min(file.Bytes.Length, HeadLength)));
        if (parser is null)
        {
            status.Show(LedgerText.Format(Strings.Import_Unsupported, file.Name), isError: true);
            return null;
        }

        var currency = account.Balance.Currency;
        var options = ImportOptions.Default with { Currency = currency };
        var result = await ParseAsync(parser, file.Bytes, options);
        if (result.IsMigration && migration is not null)
        {
            // A YNAB or Monarch export holds several accounts and categories: the migration preview takes over (M9).
            await migration.ImportParsedAsync(file, result, currency);
            return null;
        }
        if (result.CsvLayout is { } layout)
        {
            var remembered = await settings.GetCsvMappingAsync(account.Id, CancellationToken.None);
            var useRemembered = remembered is not null && remembered.Fits(layout.Headers);
            var mapping = new CsvMappingViewModel(file.Name, currency, layout, useRemembered ? remembered!.Mapping : layout.Mapping, useRemembered,
                o => ParseAsync(parser, file.Bytes, o));
            if (!await dialogs.ShowAsync(mapping) || mapping.Result is not { } chosen)
            {
                return null;
            }

            result = mapping.LastResult ?? await ParseAsync(parser, file.Bytes, options with { CsvMapping = chosen });
            await settings.SaveCsvMappingAsync(account.Id, new RememberedCsvMapping(chosen, layout.Headers), CancellationToken.None);
        }

        if (result.Transactions.Count == 0)
        {
            status.Show(LedgerText.Format(Strings.Import_NoRows, file.Name), isError: true);
            return null;
        }

        var names = await NamesAsync();
        var statements = result.Accounts.Count > 1
            ? result.Accounts.Select(a => new StatementOption(a.AccountId, StatementLabel(a))).ToList()
            : [];
        var batch = ImportBatchBuilder.FromParse(account.Id, currency, result);
        var preview = await imports.PreviewAsync(TransactionSource.File, batch, CancellationToken.None);
        var dialog = new ImportPreviewViewModel(
            file.Name,
            account.Name,
            currency,
            batch,
            preview,
            names,
            b => imports.ImportTransactionsAsync(TransactionSource.File, b, CancellationToken.None),
            statements,
            async s =>
            {
                var b = ImportBatchBuilder.FromParse(account.Id, currency, result, s.AccountId);
                return (b, await imports.PreviewAsync(TransactionSource.File, b, CancellationToken.None));
            });
        if (!await dialogs.ShowAsync(dialog) || dialog.Summary is not { } summary)
        {
            return null;
        }

        if (file.Folder is { Length: > 0 } folder)
        {
            await settings.SaveLastFolderAsync(account.Id, folder, CancellationToken.None);
        }

        status.Show(SummaryText(summary, file.Name, account.Name, currency, dialog.Rows.Count), offerUndo: summary.HasChanges);
        return summary;
    }

    /// <summary>The toast text for a summary.</summary>
    public static string SummaryText(ImportSummary summary, string fileName, string accountName, string currency, int rows)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!summary.HasChanges)
        {
            return LedgerText.Format(Strings.Import_NothingNew, fileName, rows, accountName);
        }

        var parts = new List<string>();
        void Add(int count, string template)
        {
            if (count > 0)
            {
                parts.Add(LedgerText.Format(template, count));
            }
        }

        Add(summary.Added, Strings.Import_SummaryAdded);
        Add(summary.Updated, Strings.Import_SummaryUpdated);
        Add(summary.MatchedToExisting, Strings.Import_SummaryMatched);
        Add(summary.TransfersMatched, Strings.Import_SummaryTransfers);
        Add(summary.DuplicatesSkipped, Strings.Import_SummaryDuplicates);
        Add(summary.SkippedByChoice, Strings.Import_SummarySkipped);
        Add(summary.Uncategorized, Strings.Import_SummaryUncategorized);
        if (summary.ReportedBalance is { } balance)
        {
            parts.Add(LedgerText.Format(Strings.Import_SummaryBalance, LedgerText.Money(balance.Balance, currency)));
        }

        return LedgerText.Format(Strings.Import_Summary, fileName, accountName, string.Join(", ", parts));
    }

    private static string StatementLabel(DetectedAccount account) =>
        string.Join(" · ", new[] { account.Name, account.AccountType, account.AccountId is { Length: > 4 } id ? "…" + id[^4..] : account.AccountId }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal));

    private static async Task<ParseResult> ParseAsync(IFileImportParser parser, byte[] bytes, ImportOptions options)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        return await Task.Run(() => parser.ParseAsync(stream, options));
    }

    private async Task<ImportSummary?> RunAsync(Guid? accountId)
    {
        try
        {
            var all = await accounts.GetAccountsAsync(includeClosed: false, CancellationToken.None);
            var account = accountId is { } id ? all.FirstOrDefault(a => a.Id == id) : await ChooseAccountAsync(all);
            if (account is null)
            {
                return null;
            }

            var folder = await settings.GetLastFolderAsync(account.Id, CancellationToken.None);
            PickedImportFile? file;
            try
            {
                file = await FilePicker.PickAsync(LedgerText.Format(Strings.Import_PickerTitle, account.Name), folder);
            }
            catch (IOException ex)
            {
                status.Show(LedgerText.Format(Strings.Import_ReadFailed, string.Empty, ex.Message), isError: true);
                return null;
            }

            return file is null ? null : await ImportFileAsync(account, file);
        }
        catch (LedgerValidationException ex)
        {
            status.Show(LedgerText.Error(ex.Error), isError: true);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            status.Show(LedgerText.Format(Strings.Import_Failed, ex.Message), isError: true);
            return null;
        }
    }

    private async Task<AccountDto?> ChooseAccountAsync(IReadOnlyList<AccountDto> open)
    {
        if (open.Count == 1)
        {
            return open[0];
        }

        var picker = new PickerDialogViewModel(Strings.Import_ChooseAccountTitle, Strings.Import_ChooseAccountPrompt,
            open.Select(a => new PickerItem(a.Id, a.Name)).ToList());
        return await dialogs.ShowAsync(picker) && picker.Selected is { } choice ? open.First(a => a.Id == choice.Id) : null;
    }

    private async Task<ImportPreviewNames> NamesAsync()
    {
        var accountList = await accounts.GetAccountsAsync(includeClosed: true, CancellationToken.None);
        var categoryList = await categories.GetCategoriesAsync(includeHidden: true, CancellationToken.None);
        return new ImportPreviewNames(
            accountList.ToDictionary(a => a.Id, a => a.Name),
            categoryList.ToDictionary(c => c.Id, c => string.Create(CultureInfo.CurrentCulture, $"{c.GroupName}: {c.Name}")));
    }
}
