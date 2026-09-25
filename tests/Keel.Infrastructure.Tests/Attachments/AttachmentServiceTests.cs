using System.Security.Cryptography;
using System.Text;
using Keel.Application.Attachments;
using Keel.Application.Backup;
using Keel.Application.Files;
using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Attachments;

/// <summary>F-TXN-8 attachments: content-addressed store, undoable rows, opening, orphan clean-up, backups and moves.</summary>
public sealed class AttachmentServiceTests : IAsyncLifetime
{
    private readonly TempDirectory _sources = new();
    private LedgerTestHost _host = null!;

    private static CancellationToken Ct => CancellationToken.None;

    private IAttachmentService Attachments => _host.Get<IAttachmentService>();

    private string Folder => _host.Get<IDataDirectory>().AttachmentsDirectoryFor(_host.FilePath);

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _sources.Dispose();
    }

    private async Task<string> SourceAsync(string name, string content)
    {
        var path = _sources.File(name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private async Task<TransactionDto> TransactionAsync()
    {
        var checking = await _host.CheckingAsync();
        return await _host.AddAsync(checking.Id, -4_250, "Hardware Store");
    }

    [Fact]
    public async Task Adding_stores_the_file_by_hash_once_and_the_register_counts_it()
    {
        var txn = await TransactionAsync();
        var receipt = await SourceAsync("receipt.PDF", "receipt bytes");

        var added = await Attachments.AddAsync(txn.Id, receipt, Ct);

        Attachments.Folder.ShouldBe(Folder);
        added.FileName.ShouldBe("receipt.PDF");
        added.MimeType.ShouldBe("application/pdf");
        added.Sha256.ShouldBe(Sha("receipt bytes"));
        added.Size.ShouldBe(13);
        File.ReadAllText(Path.Combine(Folder, added.Sha256)).ShouldBe("receipt bytes");

        // The same content again: one file, and the same transaction does not get it twice.
        var copy = await SourceAsync("scan.png", "receipt bytes");
        (await Attachments.AddAsync(txn.Id, copy, Ct)).Id.ShouldBe(added.Id);
        var other = await _host.AddAsync(txn.AccountId, -100, "Cafe");
        var shared = await Attachments.AddAsync(other.Id, copy, Ct);
        shared.Sha256.ShouldBe(added.Sha256);
        shared.MimeType.ShouldBe("image/png");
        Directory.EnumerateFiles(Folder).ShouldHaveSingleItem();

        (await Attachments.ListAsync(txn.Id, Ct)).ShouldBe([added]);
        var rows = await _host.Register.GetPageAsync(new RegisterFilter(), RegisterSort.Default, 0, 200, Ct);
        rows.Single(r => r.Id == txn.Id).AttachmentCount.ShouldBe(1);
        rows.Single(r => r.Id == other.Id).AttachmentCount.ShouldBe(1);
        (await _host.Register.CountAsync(new RegisterFilter(Search: "has:attachment"), Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Add_and_remove_are_undoable_and_the_file_stays_for_undo()
    {
        var txn = await TransactionAsync();
        var added = await Attachments.AddAsync(txn.Id, await SourceAsync("r.jpg", "jpeg"), Ct);
        _host.Undo.NextUndo.ShouldBe(LedgerAction.AddAttachment);

        await Attachments.RemoveAsync(added.Id, Ct);
        (await Attachments.ListAsync(txn.Id, Ct)).ShouldBeEmpty();
        File.Exists(Path.Combine(Folder, added.Sha256)).ShouldBeTrue("removing never deletes the file; undo needs it");

        _host.Undo.NextUndo.ShouldBe(LedgerAction.RemoveAttachment);
        await _host.Undo.UndoAsync(Ct);
        (await Attachments.ListAsync(txn.Id, Ct)).ShouldBe([added]);
        await _host.Undo.UndoAsync(Ct);
        (await Attachments.ListAsync(txn.Id, Ct)).ShouldBeEmpty();
        await _host.Undo.RedoAsync(Ct);
        (await Attachments.ListAsync(txn.Id, Ct)).ShouldBe([added]);

        (await Should.ThrowAsync<LedgerValidationException>(() => Attachments.RemoveAsync(Guid.NewGuid(), Ct))).Error.ShouldBe(LedgerError.AttachmentNotFound);
    }

    [Fact]
    public async Task A_missing_transaction_or_file_is_refused()
    {
        var source = await SourceAsync("r.txt", "text");
        (await Should.ThrowAsync<LedgerValidationException>(() => Attachments.AddAsync(Guid.NewGuid(), source, Ct))).Error.ShouldBe(LedgerError.TransactionNotFound);
        await Should.ThrowAsync<FileNotFoundException>(() => Attachments.AddAsync(Guid.NewGuid(), _sources.File("nope.txt"), Ct));

        var txn = await TransactionAsync();
        var added = await Attachments.AddAsync(txn.Id, source, Ct);
        File.Delete(Path.Combine(Folder, added.Sha256));
        (await Attachments.ListAsync(txn.Id, Ct)).Single().IsMissing.ShouldBeTrue();
        (await Should.ThrowAsync<LedgerValidationException>(() => Attachments.GetOpenablePathAsync(added.Id, Ct))).Error.ShouldBe(LedgerError.AttachmentFileMissing);
    }

    [Fact]
    public async Task Opening_uses_a_read_only_copy_with_the_original_name()
    {
        var txn = await TransactionAsync();
        var added = await Attachments.AddAsync(txn.Id, await SourceAsync("Receipt: March?.pdf", "pdf"), Ct);

        var path = await Attachments.GetOpenablePathAsync(added.Id, Ct);

        Path.GetFileName(path).ShouldBe("Receipt_ March_.pdf");
        File.ReadAllText(path).ShouldBe("pdf");
        new FileInfo(path).IsReadOnly.ShouldBeTrue();
        Path.GetDirectoryName(path).ShouldNotBe(Folder);
        (await Attachments.GetOpenablePathAsync(added.Id, Ct)).ShouldBe(path, "a second open reuses the copy");
    }

    [Fact]
    public async Task Clean_up_deletes_only_old_unreferenced_files()
    {
        var txn = await TransactionAsync();
        var kept = await Attachments.AddAsync(txn.Id, await SourceAsync("kept.txt", "kept"), Ct);
        var removed = await Attachments.AddAsync(txn.Id, await SourceAsync("removed.txt", "removed"), Ct);
        await Attachments.RemoveAsync(removed.Id, Ct);
        var deletedRow = await _host.AddAsync(txn.AccountId, -1, "Deleted");
        var onDeleted = await Attachments.AddAsync(deletedRow.Id, await SourceAsync("deleted.txt", "on a deleted transaction"), Ct);
        await _host.Transactions.DeleteAsync([deletedRow.Id], Ct);
        var stray = Path.Combine(Folder, Sha("never attached"));
        await File.WriteAllTextAsync(stray, "never attached");
        var partial = Path.Combine(Folder, ".abc.tmp");
        await File.WriteAllTextAsync(partial, "half");
        var foreign = Path.Combine(Folder, "notes.txt");
        await File.WriteAllTextAsync(foreign, "the user's own file");
        foreach (var file in Directory.EnumerateFiles(Folder))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
        }

        // Within the grace period the removed attachment's file stays (undo can restore the row).
        (await Attachments.CleanOrphansAsync(Ct)).ShouldBe(2);
        File.Exists(stray).ShouldBeFalse();
        File.Exists(partial).ShouldBeFalse();
        File.Exists(foreign).ShouldBeTrue("only content-addressed files are managed");
        File.Exists(Path.Combine(Folder, removed.Sha256)).ShouldBeTrue();
        File.Exists(Path.Combine(Folder, onDeleted.Sha256)).ShouldBeTrue("a deleted transaction can be restored");

        // After the grace period it goes.
        await using (var db = _host.Db())
        {
            await db.AuditEvents.Where(e => e.EntityType == "Attachment").ExecuteUpdateAsync(s => s.SetProperty(e => e.At, DateTime.UtcNow.AddDays(-IAttachmentService.OrphanGraceDays - 1)));
        }

        (await Attachments.CleanOrphansAsync(Ct)).ShouldBe(1);
        File.Exists(Path.Combine(Folder, removed.Sha256)).ShouldBeFalse();
        File.Exists(Path.Combine(Folder, kept.Sha256)).ShouldBeTrue();
    }

    [Fact]
    public async Task Backups_carry_the_attachments_and_restore_puts_them_back()
    {
        var txn = await TransactionAsync();
        var added = await Attachments.AddAsync(txn.Id, await SourceAsync("receipt.pdf", "backed up"), Ct);
        var backups = _host.Get<IBackupService>();
        var backup = await backups.BackupNowAsync(Ct);

        await Attachments.RemoveAsync(added.Id, Ct);
        Directory.Delete(Folder, recursive: true);

        await backups.RestoreAsync(backup.Path, Ct);

        (await Attachments.ListAsync(txn.Id, Ct)).ShouldBe([added]);
        File.ReadAllText(Path.Combine(Folder, added.Sha256)).ShouldBe("backed up");
        File.ReadAllText(await Attachments.GetOpenablePathAsync(added.Id, Ct)).ShouldBe("backed up");
    }

    [Fact]
    public async Task Moving_the_budget_file_copies_its_attachments()
    {
        var txn = await TransactionAsync();
        var added = await Attachments.AddAsync(txn.Id, await SourceAsync("receipt.pdf", "moved"), Ct);
        var destination = _sources.File("Moved.keel");

        await _host.Get<IDataFileMaintenance>().CopyToAsync(destination, Ct);

        var movedFolder = _host.Get<IDataDirectory>().AttachmentsDirectoryFor(destination);
        File.ReadAllText(Path.Combine(movedFolder, added.Sha256)).ShouldBe("moved");
    }

    [Fact]
    public async Task An_export_bundle_carries_the_attachment_files_into_the_new_file()
    {
        var txn = await TransactionAsync();
        var added = await Attachments.AddAsync(txn.Id, await SourceAsync("receipt.pdf", "bundled"), Ct);
        var bundle = _sources.File("export.json");
        await _host.Get<Keel.Application.Portability.IDataExportService>().ExportBundleAsync(bundle, Ct);

        var target = _sources.File("Restored.keel");
        var result = await _host.Get<Keel.Application.Portability.IBundleImportService>().ImportIntoNewFileAsync(bundle, target, Ct);

        result.AttachmentFiles.ShouldBe(1);
        var folder = _host.Get<IDataDirectory>().AttachmentsDirectoryFor(target);
        File.ReadAllText(Path.Combine(folder, added.Sha256)).ShouldBe("bundled", "the hash-named file lands where the new file's rows look for it");
        await using var db = Keel.Infrastructure.Persistence.KeelDbContextFactory.CreateForFile(target);
        (await db.Attachments.SingleAsync()).Sha256.ShouldBe(added.Sha256);
    }
}
