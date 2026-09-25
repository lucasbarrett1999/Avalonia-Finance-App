using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Keel.Desktop.Resources;

namespace Keel.Desktop.Services;

/// <summary>
/// File pickers for budget files and backups (F-SET-1), through Avalonia's storage API (PRD 8: native
/// dialogs on Windows and macOS, portals on Linux). Tests register a fake.
/// </summary>
public interface IFileDialogs
{
    /// <summary>Asks for an existing <c>.keel</c> file; null when cancelled.</summary>
    Task<string?> OpenBudgetFileAsync(string? startFolder);

    /// <summary>Asks where to save a <c>.keel</c> file (new file or new location); null when cancelled.</summary>
    Task<string?> SaveBudgetFileAsync(string title, string suggestedName, string? startFolder);

    /// <summary>Asks for a backup zip; null when cancelled.</summary>
    Task<string?> OpenBackupAsync(string? startFolder);

    /// <summary>Asks where to save a chart image (PRD 9.8, PNG export); null when cancelled.</summary>
    Task<string?> SavePngAsync(string suggestedName);
}

/// <summary>The platform pickers of the main window.</summary>
public sealed class StorageFileDialogs : IFileDialogs
{
    private static readonly FilePickerFileType BudgetFiles = new(Strings.FileDialog_BudgetFiles)
    {
        Patterns = ["*.keel", "*.KEEL"],
        MimeTypes = ["application/vnd.keel.budget", "application/x-sqlite3"],
        AppleUniformTypeIdentifiers = ["app.keel.budget", "public.data"],
    };

    private static readonly FilePickerFileType Backups = new(Strings.FileDialog_Backups)
    {
        Patterns = ["*.zip", "*.ZIP"],
        MimeTypes = ["application/zip"],
        AppleUniformTypeIdentifiers = ["public.zip-archive"],
    };

    /// <inheritdoc />
    public async Task<string?> OpenBudgetFileAsync(string? startFolder)
    {
        if (Storage() is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.FileDialog_OpenTitle,
            AllowMultiple = false,
            FileTypeFilter = [BudgetFiles, FilePickerFileTypes.All],
            SuggestedStartLocation = await StartAsync(storage, startFolder),
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task<string?> SaveBudgetFileAsync(string title, string suggestedName, string? startFolder)
    {
        if (Storage() is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "keel",
            ShowOverwritePrompt = true,
            FileTypeChoices = [BudgetFiles],
            SuggestedStartLocation = await StartAsync(storage, startFolder),
        });
        var path = file?.TryGetLocalPath();
        if (path is null)
        {
            return null;
        }

        return path.EndsWith(".keel", StringComparison.OrdinalIgnoreCase) ? path : path + ".keel";
    }

    /// <inheritdoc />
    public async Task<string?> OpenBackupAsync(string? startFolder)
    {
        if (Storage() is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.FileDialog_RestoreTitle,
            AllowMultiple = false,
            FileTypeFilter = [Backups],
            SuggestedStartLocation = await StartAsync(storage, startFolder),
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task<string?> SavePngAsync(string suggestedName)
    {
        if (Storage() is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.ExportPng_Title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "png",
            ShowOverwritePrompt = true,
            FileTypeChoices = [FilePickerFileTypes.ImagePng],
        });
        var path = file?.TryGetLocalPath();
        if (path is null)
        {
            return null;
        }

        return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? path : path + ".png";
    }

    private static IStorageProvider? Storage() =>
        ((Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as TopLevel)?.StorageProvider;

    private static async Task<IStorageFolder?> StartAsync(IStorageProvider storage, string? folder) =>
        !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? await storage.TryGetFolderFromPathAsync(folder) : null;
}
