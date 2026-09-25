using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Portability;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.ViewModels.Portability;

/// <summary>What the export writes.</summary>
public enum ExportKind
{
    /// <summary>CSV files in a zip.</summary>
    CsvZip,

    /// <summary>CSV files in a new folder.</summary>
    CsvFolder,

    /// <summary>The JSON bundle that re-imports into a new budget file.</summary>
    Bundle,
}

/// <summary>
/// Settings → Data file → Export… (F-REP-6): CSV files (zip or folder) for spreadsheets, or the JSON bundle
/// that restores the whole budget into a new file. Confirming asks where to save, then writes the export.
/// </summary>
public sealed partial class ExportDialogViewModel : DialogViewModel
{
    private readonly IDataExportService _export;
    private readonly IPortabilityDialogs _pickers;
    private readonly string _stem;
    private readonly string? _startFolder;
    private readonly TimeProvider _time;

    /// <summary>Creates the dialog.</summary>
    public ExportDialogViewModel(IDataExportService export, IPortabilityDialogs pickers, string budgetFileName, string? startFolder, TimeProvider? time = null)
    {
        _export = export;
        _pickers = pickers;
        _stem = string.IsNullOrWhiteSpace(budgetFileName) ? Strings.Export_DefaultName : Path.GetFileNameWithoutExtension(budgetFileName);
        _startFolder = startFolder;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override string Title => Strings.Export_Title;

    /// <inheritdoc />
    public override double PreferredMaxWidth => 620;

    /// <summary>The chosen kind.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCsvZip), nameof(IsCsvFolder), nameof(IsBundle), nameof(ConfirmText))]
    public partial ExportKind Kind { get; set; }

    /// <summary>CSV files in a zip.</summary>
    public bool IsCsvZip
    {
        get => Kind == ExportKind.CsvZip;
        set
        {
            if (value)
            {
                Kind = ExportKind.CsvZip;
            }
        }
    }

    /// <summary>CSV files in a folder.</summary>
    public bool IsCsvFolder
    {
        get => Kind == ExportKind.CsvFolder;
        set
        {
            if (value)
            {
                Kind = ExportKind.CsvFolder;
            }
        }
    }

    /// <summary>The JSON bundle.</summary>
    public bool IsBundle
    {
        get => Kind == ExportKind.Bundle;
        set
        {
            if (value)
            {
                Kind = ExportKind.Bundle;
            }
        }
    }

    /// <summary>"Export CSV…" or "Export bundle…".</summary>
    public string ConfirmText => Kind == ExportKind.Bundle ? Strings.Export_ConfirmBundle : Strings.Export_ConfirmCsv;

    /// <summary>The folder, zip or bundle written (after confirming).</summary>
    public string? WrittenPath { get; private set; }

    /// <summary>The status-strip text after confirming.</summary>
    public string? ResultText { get; private set; }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        var date = _time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            switch (Kind)
            {
                case ExportKind.Bundle:
                    {
                        var path = await _pickers.SaveBundleAsync($"{_stem} {date}.json", _startFolder);
                        if (path is null)
                        {
                            return false;
                        }

                        var info = await _export.ExportBundleAsync(path, CancellationToken.None);
                        WrittenPath = info.Path;
                        ResultText = LedgerText.Format(Strings.Export_BundleDone, info.Path, info.Count(nameof(Keel.Domain.Entities.Transaction)));
                        return true;
                    }

                case ExportKind.CsvZip:
                    {
                        var path = await _pickers.SaveExportZipAsync(LedgerText.Format(Strings.Export_FolderName, _stem, date) + ".zip", _startFolder);
                        if (path is null)
                        {
                            return false;
                        }

                        var result = await _export.ExportCsvAsync(path, asZip: true, CancellationToken.None);
                        WrittenPath = result.Path;
                        ResultText = LedgerText.Format(Strings.Export_CsvDone, result.Path, result.Rows.GetValueOrDefault(CsvExportFiles.Transactions));
                        return true;
                    }

                default:
                    {
                        var folder = await _pickers.PickExportFolderAsync(_startFolder);
                        if (folder is null)
                        {
                            return false;
                        }

                        var target = UniqueFolder(Path.Combine(folder, LedgerText.Format(Strings.Export_FolderName, _stem, date)));
                        var result = await _export.ExportCsvAsync(target, asZip: false, CancellationToken.None);
                        WrittenPath = result.Path;
                        ResultText = LedgerText.Format(Strings.Export_CsvDone, result.Path, result.Rows.GetValueOrDefault(CsvExportFiles.Transactions));
                        return true;
                    }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException)
        {
            Error = LedgerText.Format(Strings.Export_Failed, ex.Message);
            return false;
        }
    }

    private static string UniqueFolder(string path)
    {
        var candidate = path;
        for (var i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
        {
            candidate = path + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
        }

        return candidate;
    }
}
