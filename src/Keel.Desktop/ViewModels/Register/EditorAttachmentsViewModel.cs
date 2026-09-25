using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Attachments;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Register;

/// <summary>What the editor's attachment list needs from the app.</summary>
/// <param name="Service">The attachment store.</param>
/// <param name="Files">File picker and the system launcher.</param>
/// <param name="Status">The status strip (undo toasts and errors).</param>
public sealed record AttachmentContext(IAttachmentService Service, IAttachmentFiles Files, StatusService Status);

/// <summary>
/// The attachment list of the transaction editor (F-TXN-8, ADR 0097): add from the file picker or by dropping
/// files on the editor, open with the system's default app, remove. On an existing transaction each add and
/// remove is its own undoable action right away; on a new one the files wait and are attached after the save.
/// </summary>
public sealed partial class EditorAttachmentsViewModel : ObservableObject
{
    private readonly AttachmentContext _context;

    /// <summary>Creates the list for <paramref name="transactionId"/> (null for a new transaction).</summary>
    public EditorAttachmentsViewModel(AttachmentContext context, Guid? transactionId)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        TransactionId = transactionId;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>The transaction, or null while it is new.</summary>
    public Guid? TransactionId { get; }

    /// <summary>Attached (or waiting) files.</summary>
    public ObservableCollection<AttachmentRowViewModel> Items { get; } = [];

    /// <summary>Whether any file is attached or waiting.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>A problem with the last action, shown under the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    /// <summary>Whether <see cref="Error"/> is set.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Files waiting for a new transaction to be saved.</summary>
    public IReadOnlyList<string> PendingPaths => Items.Where(i => i.PendingPath is not null).Select(i => i.PendingPath!).ToList();

    /// <summary>The latest action (tests await it).</summary>
    public Task Working { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the attachments of an existing transaction.</summary>
    public async Task LoadAsync()
    {
        if (TransactionId is not { } id)
        {
            return;
        }

        var rows = await Task.Run(() => _context.Service.ListAsync(id, CancellationToken.None));
        Items.Clear();
        foreach (var row in rows)
        {
            Items.Add(new AttachmentRowViewModel(row, null, this));
        }
    }

    /// <summary>Attaches files (picker or drop).</summary>
    public Task AddFilesAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Working = AddCoreAsync(paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList());
    }

    [RelayCommand]
    private async Task AttachAsync()
    {
        var paths = await _context.Files.PickAsync();
        await AddFilesAsync(paths);
    }

    [RelayCommand]
    private Task OpenAsync(AttachmentRowViewModel? row) => row is null ? Task.CompletedTask : Working = OpenCoreAsync(row);

    [RelayCommand]
    private Task RemoveAsync(AttachmentRowViewModel? row) => row is null ? Task.CompletedTask : Working = RemoveCoreAsync(row);

    private async Task AddCoreAsync(IReadOnlyList<string> paths)
    {
        Error = null;
        if (paths.Count == 0)
        {
            return;
        }

        if (TransactionId is not { } id)
        {
            foreach (var path in paths.Where(p => Items.All(i => !string.Equals(i.PendingPath, p, StringComparison.Ordinal))))
            {
                Items.Add(new AttachmentRowViewModel(null, path, this));
            }

            return;
        }

        var added = 0;
        foreach (var path in paths)
        {
            try
            {
                var row = await Task.Run(() => _context.Service.AddAsync(id, path, CancellationToken.None));
                if (Items.All(i => i.Id != row.Id))
                {
                    Items.Add(new AttachmentRowViewModel(row, null, this));
                    added++;
                }
            }
            catch (LedgerValidationException ex)
            {
                Error = LedgerText.Error(ex.Error);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = LedgerText.Format(Strings.Attachment_AddFailed, Path.GetFileName(path));
            }
        }

        if (added > 0)
        {
            _context.Status.Show(LedgerText.Format(Strings.Attachment_Added, added.ToString(CultureInfo.CurrentCulture)), offerUndo: true);
        }
    }

    private async Task OpenCoreAsync(AttachmentRowViewModel row)
    {
        Error = null;
        try
        {
            var path = row.PendingPath ?? await Task.Run(() => _context.Service.GetOpenablePathAsync(row.Id, CancellationToken.None));
            if (!await _context.Files.OpenAsync(path))
            {
                Error = LedgerText.Format(Strings.Attachment_OpenFailed, row.FileName);
            }
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = LedgerText.Format(Strings.Attachment_OpenFailed, row.FileName);
        }
    }

    private async Task RemoveCoreAsync(AttachmentRowViewModel row)
    {
        Error = null;
        if (row.PendingPath is not null)
        {
            Items.Remove(row);
            return;
        }

        try
        {
            await Task.Run(() => _context.Service.RemoveAsync(row.Id, CancellationToken.None));
            Items.Remove(row);
            _context.Status.Show(LedgerText.Format(Strings.Attachment_Removed, row.FileName), offerUndo: true);
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
        }
    }
}

/// <summary>One attached (or waiting) file.</summary>
public sealed class AttachmentRowViewModel
{
    /// <summary>Creates a row for a stored attachment or a file waiting for the save.</summary>
    public AttachmentRowViewModel(AttachmentDto? attachment, string? pendingPath, EditorAttachmentsViewModel owner)
    {
        Attachment = attachment;
        PendingPath = pendingPath;
        Owner = owner;
    }

    /// <summary>The stored attachment, or null while waiting.</summary>
    public AttachmentDto? Attachment { get; }

    /// <summary>The file to attach after the save, or null.</summary>
    public string? PendingPath { get; }

    /// <summary>The list (the row's buttons bind to its commands).</summary>
    public EditorAttachmentsViewModel Owner { get; }

    /// <summary>Attachment id (empty while waiting).</summary>
    public Guid Id => Attachment?.Id ?? Guid.Empty;

    /// <summary>File name.</summary>
    public string FileName => Attachment?.FileName ?? Path.GetFileName(PendingPath ?? string.Empty);

    /// <summary>Whether the file waits for the transaction to be saved.</summary>
    public bool IsPending => PendingPath is not null;

    /// <summary>Whether the stored file is missing from the attachments folder.</summary>
    public bool IsMissing => Attachment is { IsMissing: true };

    /// <summary>Size, "attached when saved", or "file missing".</summary>
    public string Detail => IsPending ? Strings.Attachment_Pending
        : IsMissing ? Strings.Attachment_Missing
        : FormatSize(Attachment!.Size ?? 0);

    /// <summary>"Open receipt.pdf".</summary>
    public string OpenName => LedgerText.Format(Strings.Attachment_OpenName, FileName);

    /// <summary>"Remove receipt.pdf".</summary>
    public string RemoveName => LedgerText.Format(Strings.Attachment_RemoveName, FileName);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => LedgerText.Format(Strings.Attachment_SizeBytes, bytes.ToString("N0", CultureInfo.CurrentCulture)),
        < 1024 * 1024 => LedgerText.Format(Strings.Attachment_SizeKb, (bytes / 1024d).ToString("N0", CultureInfo.CurrentCulture)),
        _ => LedgerText.Format(Strings.Attachment_SizeMb, (bytes / (1024d * 1024d)).ToString("N1", CultureInfo.CurrentCulture)),
    };
}
