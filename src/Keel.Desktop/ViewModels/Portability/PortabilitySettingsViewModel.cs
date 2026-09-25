using CommunityToolkit.Mvvm.Input;
using Keel.Application.Files;
using Keel.Application.Portability;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Portability;

/// <summary>
/// Settings → Data file (F-REP-6, PRD 9.10): Export… (CSV files or the JSON bundle), Import bundle into a new
/// file… (restores a bundle into a new budget file and opens it in a new session, ADR 0080) and Import from YNAB
/// or Monarch….
/// </summary>
public sealed partial class PortabilitySettingsViewModel(
    AppSession session,
    IDataExportService export,
    IBundleImportService bundles,
    IPortabilityDialogs pickers,
    IFileDialogs files,
    IDataDirectory data,
    BudgetSessions sessions,
    DialogService dialogs,
    StatusService status,
    MigrationWorkflow migration) : ViewModelBase
{
    /// <summary>Whether a budget file is open (export needs one).</summary>
    public bool HasFile => session.BudgetFile is not null;

    /// <summary>The migration flow (tests replace its file picker).</summary>
    public MigrationWorkflow Migration => migration;

    /// <summary>The latest action (tests await it).</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Opens the export dialog.</summary>
    [RelayCommand]
    public Task ExportAsync() => Running = ExportCoreAsync();

    /// <summary>Imports a bundle into a new budget file and opens it.</summary>
    [RelayCommand]
    public Task ImportBundleAsync() => Running = ImportBundleCoreAsync();

    /// <summary>Imports a YNAB or Monarch export into the open file.</summary>
    [RelayCommand]
    public Task ImportFromAppAsync() => Running = migration.ImportAsync();

    private async Task ExportCoreAsync()
    {
        if (session.BudgetFile is not { } file)
        {
            return;
        }

        var dialog = new ExportDialogViewModel(export, pickers, file.FileName, null);
        if (await dialogs.ShowAsync(dialog) && dialog.ResultText is { } text)
        {
            status.Show(text);
        }
    }

    private async Task ImportBundleCoreAsync()
    {
        var path = await pickers.OpenBundleAsync(null);
        if (path is null)
        {
            return;
        }

        BundleInfo info;
        try
        {
            info = await bundles.ReadInfoAsync(path, CancellationToken.None);
        }
        catch (BundleException ex)
        {
            status.Show(PortabilityText.BundleError(ex), isError: true);
            return;
        }

        var dialog = new BundleImportDialogViewModel(info, bundles, files, data.BudgetsDirectory);
        if (!await dialogs.ShowAsync(dialog) || dialog.Result is not { } result)
        {
            return;
        }

        try
        {
            await sessions.OpenAsync(result.Path, new BudgetStartupOptions(Message: LedgerText.Format(Strings.Bundle_RestoredStatus, Path.GetFileName(result.Path), result.Count(nameof(Keel.Domain.Entities.Transaction)))));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status.Show(LedgerText.Format(Strings.Shell_StatusOpenRequestedFailed, Path.GetFileName(result.Path), ex.Message), isError: true);
        }
    }
}
