using Keel.Application.Accounts;
using Keel.Application.Import;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Import;

namespace Keel.Desktop.ViewModels.Portability;

/// <summary>
/// Moving to Keel from YNAB or Monarch (PRD 9.10 step 1, ADR 0099): pick the export, recognize it, show the
/// migration preview (accounts, categories, tags, a live dry run) or, for a YNAB budget export, the assignments
/// it writes, then import through <see cref="IMigrationImportService"/> with an Undo toast. Reached from
/// Settings → Data file, the command palette, the first-run Welcome step, and the register's "Import file"
/// when the chosen file turns out to be such an export.
/// </summary>
public sealed class MigrationWorkflow(
    IFileImportParserResolver parsers,
    IMigrationImportService migration,
    IAccountService accounts,
    DialogService dialogs,
    StatusService status,
    IImportFilePicker picker)
{
    private const int HeadLength = 4096;

    /// <summary>The file picker (tests replace it).</summary>
    public IImportFilePicker FilePicker { get; set; } = picker;

    /// <summary>The latest run (tests await it); true when something was imported.</summary>
    public Task<bool> Running { get; private set; } = Task.FromResult(false);

    /// <summary>Asks for the export file and imports it.</summary>
    public Task<bool> ImportAsync(string? currency = null)
    {
        Running = PickAndImportAsync(currency);
        return Running;
    }

    /// <summary>Parses an already-read file (the budget currency when none is given) and, when it is a YNAB or Monarch export, imports it.</summary>
    public async Task<bool> ImportFileAsync(PickedImportFile file, string? currency = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        var budgetCurrency = currency ?? await BudgetCurrencyAsync();
        var parsed = await ParseAsync(parsers, file, budgetCurrency);
        if (parsed is null || !parsed.IsMigration)
        {
            status.Show(LedgerText.Format(Strings.Ynab_NotAnExport, file.Name), isError: true);
            return false;
        }

        return await ImportParsedAsync(file, parsed, budgetCurrency);
    }

    /// <summary>Shows the preview for a parsed YNAB or Monarch export and imports it when confirmed.</summary>
    public async Task<bool> ImportParsedAsync(PickedImportFile file, ParseResult parsed, string currency)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(parsed);
        try
        {
            if (parsed.Format == ImportFileFormat.YnabBudget)
            {
                if (parsed.BudgetRows.Count == 0)
                {
                    status.Show(LedgerText.Format(Strings.Import_NoRows, file.Name), isError: true);
                    return false;
                }

                var preview = await migration.PreviewBudgetAsync(parsed, CancellationToken.None);
                var budget = new BudgetImportDialogViewModel(file.Name, parsed, preview, migration);
                if (!await dialogs.ShowAsync(budget) || budget.Summary is not { } written)
                {
                    return false;
                }

                status.Show(LedgerText.Format(Strings.Ynab_BudgetDone, file.Name, written.Assignments, written.Months), offerUndo: written.HasChanges);
                return written.HasChanges;
            }

            if (parsed.Transactions.Count == 0)
            {
                status.Show(LedgerText.Format(Strings.Import_NoRows, file.Name), isError: true);
                return false;
            }

            var plan = await migration.PlanAsync(parsed, CancellationToken.None);
            var open = await accounts.GetAccountsAsync(includeClosed: false, CancellationToken.None);
            var dialog = new MigrationDialogViewModel(file.Name, parsed, plan, open, currency, migration);
            if (!await dialogs.ShowAsync(dialog) || dialog.Summary is not { } summary)
            {
                return false;
            }

            status.Show(PortabilityText.MigrationSummary(summary, file.Name), offerUndo: summary.HasChanges);
            return summary.HasChanges;
        }
        catch (LedgerValidationException ex)
        {
            status.Show(LedgerText.Error(ex.Error), isError: true);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Data.Common.DbException)
        {
            status.Show(LedgerText.Format(Strings.Import_Failed, ex.Message), isError: true);
            return false;
        }
    }

    /// <summary>Reads a picked file with the parser its content calls for; null when no parser reads it.</summary>
    public static async Task<ParseResult?> ParseAsync(IFileImportParserResolver parsers, PickedImportFile file, string currency)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        ArgumentNullException.ThrowIfNull(file);
        var parser = parsers.Resolve(file.Name, file.Bytes.AsSpan(0, Math.Min(file.Bytes.Length, HeadLength)));
        if (parser is null)
        {
            return null;
        }

        await using var stream = new MemoryStream(file.Bytes, writable: false);
        return await Task.Run(() => parser.ParseAsync(stream, ImportOptions.Default with { Currency = currency }));
    }

    private async Task<bool> PickAndImportAsync(string? currency)
    {
        PickedImportFile? file;
        try
        {
            file = await FilePicker.PickAsync(Strings.Ynab_PickerTitle, null);
        }
        catch (IOException ex)
        {
            status.Show(LedgerText.Format(Strings.Import_ReadFailed, string.Empty, ex.Message), isError: true);
            return false;
        }

        return file is not null && await ImportFileAsync(file, currency);
    }

    // The budget's currency: the most common currency of the open on-budget accounts, else the OS region's.
    private async Task<string> BudgetCurrencyAsync()
    {
        var list = await accounts.GetAccountsAsync(includeClosed: false, CancellationToken.None);
        return list.Where(a => a.IsOnBudget).GroupBy(a => a.Balance.Currency).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
            ?? FirstRun.FirstRunViewModel.CurrencyFor(System.Globalization.RegionInfo.CurrentRegion);
    }
}
