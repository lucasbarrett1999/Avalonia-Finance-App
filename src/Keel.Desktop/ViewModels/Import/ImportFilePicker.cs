using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Keel.Desktop.Resources;

namespace Keel.Desktop.ViewModels.Import;

/// <summary>A file the user picked for import, read into memory.</summary>
/// <param name="Name">File name with extension.</param>
/// <param name="Folder">Local folder it came from, when known (remembered per account).</param>
/// <param name="Bytes">Contents.</param>
public sealed record PickedImportFile(string Name, string? Folder, byte[] Bytes);

/// <summary>Asks the user for an import file; replaceable in tests.</summary>
public interface IImportFilePicker
{
    /// <summary>Shows the picker; null when cancelled.</summary>
    /// <param name="title">Picker title.</param>
    /// <param name="startFolder">Folder to open in, when known.</param>
    Task<PickedImportFile?> PickAsync(string title, string? startFolder);
}

/// <summary>
/// The platform file picker through Avalonia's storage API (native dialogs on Windows, macOS and
/// Linux portals), filtered to CSV, OFX, QFX and QIF.
/// </summary>
public sealed class StorageImportFilePicker : IImportFilePicker
{
    /// <summary>Extensions offered (both cases for case-sensitive Linux pickers).</summary>
    public static IReadOnlyList<string> Patterns { get; } =
        ["*.csv", "*.ofx", "*.qfx", "*.qif", "*.CSV", "*.OFX", "*.QFX", "*.QIF"];

    /// <inheritdoc />
    public async Task<PickedImportFile?> PickAsync(string title, string? startFolder)
    {
        var topLevel = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as TopLevel;
        if (topLevel?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.Import_FileTypeName)
                {
                    Patterns = Patterns,
                    MimeTypes = ["text/csv", "application/x-ofx", "application/vnd.intu.qfx", "application/qif"],
                    AppleUniformTypeIdentifiers = ["public.comma-separated-values-text", "public.data"],
                },
                FilePickerFileTypes.All,
            ],
        };
        if (!string.IsNullOrEmpty(startFolder) && Directory.Exists(startFolder))
        {
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(startFolder);
        }

        var files = await storage.OpenFilePickerAsync(options);
        if (files.Count == 0)
        {
            return null;
        }

        var file = files[0];
        await using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var path = file.TryGetLocalPath();
        return new PickedImportFile(file.Name, path is null ? null : Path.GetDirectoryName(path), buffer.ToArray());
    }
}
