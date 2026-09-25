using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Import;
using Keel.Application.Portability;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Budget;
using Keel.Desktop.ViewModels.Import;
using Keel.Desktop.ViewModels.Portability;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.ViewModels.FirstRun;

/// <summary>
/// The Welcome step's other ways in (M9, ADR 0100): restore a Keel export bundle into the new file (Open
/// existing), or create the file and import a YNAB or Monarch export into it (PRD 9.10 step 1).
/// </summary>
public sealed partial class FirstRunViewModel
{
    private readonly IBundleImportService? _bundles;
    private readonly IPortabilityDialogs? _portability;
    private readonly IFileImportParserResolver? _parsers;

    /// <summary>The picker for YNAB and Monarch exports (tests replace it).</summary>
    public IImportFilePicker? ImportPicker { get; set; }

    /// <summary>Whether this build can restore bundles and import other apps' exports here.</summary>
    public bool CanMigrate => _bundles is not null && _portability is not null && _parsers is not null && ImportPicker is not null;

    /// <summary>The bundle chosen to restore (its header), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingBundle), nameof(PendingBundleSource), nameof(PendingBundleCounts))]
    public partial BundleInfo? PendingBundle { get; private set; }

    /// <summary>Whether a bundle waits for "Restore".</summary>
    public bool HasPendingBundle => PendingBundle is not null;

    /// <summary>"Exported from … on …".</summary>
    public string PendingBundleSource => PendingBundle is { } info ? PortabilityText.BundleSource(info) : string.Empty;

    /// <summary>"8 accounts · 3,000 transactions · …".</summary>
    public string PendingBundleCounts => PendingBundle is { } info ? PortabilityText.BundleCounts(info) : string.Empty;

    /// <summary>Open existing: choose a Keel export bundle to restore into a new budget file.</summary>
    [RelayCommand]
    public Task ChooseBundleAsync() => Running = RunAsync(async () =>
    {
        if (_portability is null || _bundles is null)
        {
            return;
        }

        var path = await _portability.OpenBundleAsync(null);
        if (path is null)
        {
            return;
        }

        try
        {
            var info = await _bundles.ReadInfoAsync(path, CancellationToken.None);
            PendingBundle = info;
            FileName = BundleImportDialogViewModel.DefaultName(info);
        }
        catch (BundleException ex)
        {
            Error = PortabilityText.BundleError(ex);
        }
    });

    /// <summary>Restores the chosen bundle into <see cref="NewFilePath"/> and opens it; the setup ends.</summary>
    [RelayCommand]
    public Task RestoreBundleAsync() => Running = RunAsync(async () =>
    {
        if (PendingBundle is not { } info || _bundles is null)
        {
            return;
        }

        var path = NewFilePath;
        if (File.Exists(path))
        {
            Error = LedgerText.Format(Strings.DataFile_Exists, Path.GetFileName(path));
            return;
        }

        BundleImportResult result;
        try
        {
            result = await _bundles.ImportIntoNewFileAsync(info.Path, path, CancellationToken.None);
        }
        catch (BundleException ex)
        {
            Error = PortabilityText.BundleError(ex);
            return;
        }

        await _sessions.OpenAsync(path, new BudgetStartupOptions(Message: LedgerText.Format(Strings.Bundle_RestoredStatus, Path.GetFileName(path), result.Count(nameof(Keel.Domain.Entities.Transaction)))));
        _settings.Update(s => s with { FirstRunCompleted = true });
    });

    /// <summary>Forgets the chosen bundle.</summary>
    [RelayCommand]
    public void CancelBundle() => PendingBundle = null;

    /// <summary>
    /// Import from YNAB or Monarch: choose the export, create the budget file named above, and import the export
    /// into it through the migration preview; lands on Budget when something was imported.
    /// </summary>
    [RelayCommand]
    public Task ImportFromAppAsync() => Running = RunAsync(async () =>
    {
        if (ImportPicker is null || _parsers is null)
        {
            return;
        }

        var file = await ImportPicker.PickAsync(Strings.Ynab_PickerTitle, null);
        if (file is null)
        {
            return;
        }

        var currency = string.IsNullOrWhiteSpace(Currency) ? Keel.Domain.Currency.Default : Currency.Trim().ToUpperInvariant();
        var parsed = await MigrationWorkflow.ParseAsync(_parsers, file, currency);
        if (parsed is null || !parsed.IsMigration)
        {
            Error = LedgerText.Format(Strings.Ynab_NotAnExport, file.Name);
            return;
        }

        var path = NewFilePath;
        if (File.Exists(path))
        {
            Error = LedgerText.Format(Strings.DataFile_Exists, Path.GetFileName(path));
            return;
        }

        await _sessions.OpenAsync(path, new BudgetStartupOptions(Message: Strings.DataFile_CreatedStatus));
        _settings.Update(s => s with { FirstRunCompleted = true });
        if (_sessions.Current?.Services is not { } services)
        {
            return;
        }

        if (await services.GetRequiredService<MigrationWorkflow>().ImportParsedAsync(file, parsed, currency))
        {
            services.GetRequiredService<ShellViewModel>().NavigateTo<BudgetViewModel>();
        }
    });
}
