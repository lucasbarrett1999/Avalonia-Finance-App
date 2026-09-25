using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Files;
using Keel.Application.Portability;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.ViewModels.Portability;

/// <summary>
/// Settings → Data file → Import bundle into a new file… (F-REP-6): what the bundle holds, the new file's name
/// and folder, then the import. A bundle never goes into the open file or any file with data (ADR 0098);
/// the caller opens the new file in a new session.
/// </summary>
public sealed partial class BundleImportDialogViewModel : DialogViewModel
{
    private readonly IBundleImportService _bundles;
    private readonly IFileDialogs _files;

    /// <summary>Creates the dialog.</summary>
    public BundleImportDialogViewModel(BundleInfo info, IBundleImportService bundles, IFileDialogs files, string folder)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        _bundles = bundles;
        _files = files;
        FileFolder = folder;
        FileName = DefaultName(info);
    }

    /// <inheritdoc />
    public override string Title => Strings.Bundle_Title;

    /// <inheritdoc />
    public override double PreferredMaxWidth => 620;

    /// <summary>The bundle.</summary>
    public BundleInfo Info { get; }

    /// <summary>"Exported from … on … by Keel …".</summary>
    public string SourceText => PortabilityText.BundleSource(Info);

    /// <summary>"8 accounts · 3,000 transactions · …".</summary>
    public string CountsText => PortabilityText.BundleCounts(Info);

    /// <summary>Name of the new file (without extension).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewFilePath))]
    public partial string FileName { get; set; }

    /// <summary>Folder of the new file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewFilePath))]
    public partial string FileFolder { get; set; }

    /// <summary>Where the new file will be created.</summary>
    public string NewFilePath => Path.Combine(FileFolder, (string.IsNullOrWhiteSpace(FileName) ? DefaultName(Info) : FileName.Trim()) + IBudgetFileService.Extension);

    /// <summary>The imported file (after confirming).</summary>
    public BundleImportResult? Result { get; private set; }

    /// <summary>The new file's name from the bundle's source file ("Household (restored)").</summary>
    public static string DefaultName(BundleInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var stem = Path.GetFileNameWithoutExtension(info.SourceFile);
        return LedgerText.Format(Strings.Bundle_RestoredName, string.IsNullOrWhiteSpace(stem) ? Strings.Export_DefaultName : stem);
    }

    /// <summary>Chooses another folder or name.</summary>
    [RelayCommand]
    public async Task ChooseLocationAsync()
    {
        var path = await _files.SaveBudgetFileAsync(Strings.Bundle_LocationTitle, Path.GetFileName(NewFilePath), FileFolder);
        if (path is not null)
        {
            FileFolder = Path.GetDirectoryName(path) ?? FileFolder;
            FileName = Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        var path = NewFilePath;
        if (File.Exists(path))
        {
            Error = LedgerText.Format(Strings.Bundle_FileExists, Path.GetFileName(path));
            return false;
        }

        try
        {
            Result = await _bundles.ImportIntoNewFileAsync(Info.Path, path, CancellationToken.None);
            return true;
        }
        catch (BundleException ex)
        {
            Error = PortabilityText.BundleError(ex);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            Error = LedgerText.Format(Strings.Bundle_Failed, ex.Message);
            return false;
        }
    }
}
