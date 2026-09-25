using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Keel.Desktop.Resources;

namespace Keel.Desktop.Services;

/// <summary>
/// File pickers of the full export and the bundle import (F-REP-6), through Avalonia's storage API (native
/// dialogs, Linux portals). Tests register a fake.
/// </summary>
public interface IPortabilityDialogs
{
    /// <summary>Asks for the folder a CSV export goes into; null when cancelled.</summary>
    Task<string?> PickExportFolderAsync(string? startFolder);

    /// <summary>Asks where to save the CSV zip; null when cancelled.</summary>
    Task<string?> SaveExportZipAsync(string suggestedName, string? startFolder);

    /// <summary>Asks where to save the JSON bundle; null when cancelled.</summary>
    Task<string?> SaveBundleAsync(string suggestedName, string? startFolder);

    /// <summary>Asks for a JSON bundle to import; null when cancelled.</summary>
    Task<string?> OpenBundleAsync(string? startFolder);
}

/// <summary>The platform pickers of the main window.</summary>
public sealed class StoragePortabilityDialogs : IPortabilityDialogs
{
    private static readonly FilePickerFileType Bundles = new(Strings.Bundle_FileType)
    {
        Patterns = ["*.json", "*.JSON"],
        MimeTypes = ["application/json"],
        AppleUniformTypeIdentifiers = ["public.json"],
    };

    private static readonly FilePickerFileType Zips = new(Strings.Export_ZipFileType)
    {
        Patterns = ["*.zip", "*.ZIP"],
        MimeTypes = ["application/zip"],
        AppleUniformTypeIdentifiers = ["public.zip-archive"],
    };

    /// <inheritdoc />
    public async Task<string?> PickExportFolderAsync(string? startFolder)
    {
        if (Storage() is not { CanPickFolder: true } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Export_FolderPickerTitle,
            AllowMultiple = false,
            SuggestedStartLocation = await StartAsync(storage, startFolder),
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    /// <inheritdoc />
    public Task<string?> SaveExportZipAsync(string suggestedName, string? startFolder) => SaveAsync(Strings.Export_ZipPickerTitle, suggestedName, "zip", Zips, startFolder);

    /// <inheritdoc />
    public Task<string?> SaveBundleAsync(string suggestedName, string? startFolder) => SaveAsync(Strings.Export_BundlePickerTitle, suggestedName, "json", Bundles, startFolder);

    /// <inheritdoc />
    public async Task<string?> OpenBundleAsync(string? startFolder)
    {
        if (Storage() is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Bundle_PickerTitle,
            AllowMultiple = false,
            FileTypeFilter = [Bundles, FilePickerFileTypes.All],
            SuggestedStartLocation = await StartAsync(storage, startFolder),
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private static async Task<string?> SaveAsync(string title, string suggestedName, string extension, FilePickerFileType type, string? startFolder)
    {
        if (Storage() is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [type],
            SuggestedStartLocation = await StartAsync(storage, startFolder),
        });
        var path = file?.TryGetLocalPath();
        return path is null || path.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase) ? path : path + "." + extension;
    }

    private static IStorageProvider? Storage() =>
        ((Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as TopLevel)?.StorageProvider;

    private static async Task<IStorageFolder?> StartAsync(IStorageProvider storage, string? folder) =>
        !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? await storage.TryGetFolderFromPathAsync(folder) : null;
}
