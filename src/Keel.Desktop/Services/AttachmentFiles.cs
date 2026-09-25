using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Keel.Desktop.Resources;

namespace Keel.Desktop.Services;

/// <summary>
/// The platform side of attachments (F-TXN-8): the file picker for receipts and opening a file with the
/// system's default app, through Avalonia's storage and launcher APIs (PRD 8; the same launcher the browser
/// links use). Tests register a fake.
/// </summary>
public interface IAttachmentFiles
{
    /// <summary>Asks for one or more files to attach; empty when cancelled.</summary>
    Task<IReadOnlyList<string>> PickAsync();

    /// <summary>Opens a local file with the system's default app; false when the platform could not.</summary>
    Task<bool> OpenAsync(string path);
}

/// <summary>The main window's storage provider and launcher.</summary>
public sealed class StorageAttachmentFiles : IAttachmentFiles
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> PickAsync()
    {
        if (TopLevel() is not { StorageProvider: { CanOpen: true } storage })
        {
            return [];
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Attachment_PickTitle,
            AllowMultiple = true,
            FileTypeFilter = [FilePickerFileTypes.ImageAll, FilePickerFileTypes.Pdf, FilePickerFileTypes.All],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    /// <inheritdoc />
    public async Task<bool> OpenAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return TopLevel()?.Launcher is { } launcher && await launcher.LaunchFileInfoAsync(new FileInfo(path));
    }

    private static TopLevel? TopLevel() =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
