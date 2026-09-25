using Keel.Application.Import;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.ViewModels.Portability;

/// <summary>
/// A YNAB budget export (ADR 0099): what the import would write (assigned amounts per category and month,
/// categories to create), then the import as one undoable budget action.
/// </summary>
public sealed class BudgetImportDialogViewModel : DialogViewModel
{
    private readonly IMigrationImportService _service;
    private readonly ParseResult _parsed;
    private readonly string _fileName;

    /// <summary>Creates the dialog from a dry run.</summary>
    public BudgetImportDialogViewModel(string fileName, ParseResult parsed, BudgetImportSummary preview, IMigrationImportService service)
    {
        _fileName = fileName;
        _parsed = parsed;
        _service = service;
        Preview = preview;
    }

    /// <inheritdoc />
    public override string Title => LedgerText.Format(Strings.Ynab_BudgetTitle, _fileName);

    /// <summary>The dry run.</summary>
    public BudgetImportSummary Preview { get; }

    /// <summary>What the import writes.</summary>
    public string PreviewText => PortabilityText.BudgetSummary(Preview);

    /// <summary>Whether there is anything to write.</summary>
    public bool CanImport => Preview.HasChanges;

    /// <summary>The import's result after confirming.</summary>
    public BudgetImportSummary? Summary { get; private set; }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        try
        {
            Summary = await _service.ImportBudgetAsync(_parsed, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            Error = LedgerText.Format(Strings.Import_Failed, ex.Message);
            return false;
        }
    }
}
