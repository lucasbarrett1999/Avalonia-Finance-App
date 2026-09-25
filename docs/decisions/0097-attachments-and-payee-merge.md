# 97. Attachments on transactions and payee merge

- Status: Accepted
- Date: 2026-09-25
- Milestone: M9 (stream C, F-TXN-8, F-TXN-9)

## Context

F-TXN-8 asks for receipt images and PDFs "stored in an `attachments/` folder next to the DB, referenced by
hash"; PRD 6.2 says `<datadir>/attachments/<sha256>` and PRD 10 says attachments are not encrypted. M8
already carries a per-file attachments folder, `Name.keel-attachments` next to the budget file
(`IDataDirectory.AttachmentsDirectoryFor`), through backups, restore and "Move budget file" (ADR 0081).
F-TXN-9 asks for payee merge. Payees are referenced by transactions, scheduled transactions and recurring
items (the last two with a restricting foreign key), and by rules as text.

## Decision

### Attachments

- **Folder.** Files go to `Name.keel-attachments/` next to the budget file, not a shared `attachments/`:
  two budget files in one folder must not share or clean each other's files, and backups, restore and moves
  already handle this folder. (Deviation from the literal PRD path.)
- **Content-addressed.** A file is copied in under a temporary name while its SHA-256 is computed, then
  renamed to the lower-case hex hash (identical content is stored once). The `Attachment` row keeps the
  original file name (cut to 260 characters keeping the extension), the hash and a MIME type from the
  extension. The same content on the same transaction is attached once.
- **Order of writes.** The file copy happens before the row is written in a `LedgerWriter` action
  (`AddAttachment`, undoable), so a row never points at a missing file; a copy whose row was never written
  becomes an orphan. Removing (`RemoveAttachment`, undoable) deletes the row only.
- **Orphan clean-up.** The daily maintenance job (`MaintenanceJobs`) calls `CleanOrphansAsync`: it deletes
  hash-named files (and stale partial copies) that no row refers to, including rows of soft-deleted
  transactions, unless an `Attachment` audit event of the last 30 days names the hash (undo and redo can
  bring such a row back) or the file was written in the last day. Other files in the folder are never
  touched. Purging deleted transactions removes their tag and attachment rows explicitly so undo restores
  them.
- **Opening.** Stored files have no extension and must not be edited in place, so opening copies the file
  (read-only) under its original, sanitized name to `<temp>/keel-attachments/<hash prefix>/` and launches
  it with Avalonia's launcher (`IAttachmentFiles`, the same `ILauncher` the browser links use).
- **Editor.** On an existing transaction each attach and remove is its own undoable action right away;
  on a new transaction picked or dropped files wait and are attached right after the save. Files can be
  dropped anywhere on the inline editor.
- Attachments are not encrypted (PRD 10); the backups and data guide says so.

### Payee merge

- `IPayeeService.PreviewMergeAsync`/`MergeAsync(payeeIds, survivorId)`: one `MergePayees` ledger action moves
  every transaction (soft-deleted ones included, so a restore never points at a removed payee), scheduled
  transaction and recurring item of the merged payees to the survivor, and removes the merged payees. The
  bank descriptor (`PayeeRaw`) is kept. Transfer payees cannot be merged.
- **Default category:** the survivor's wins; if it has none, the first merged payee (in list order) that has
  one gives it.
- **Rules** name payees by text: "set payee to X" and "payee equals X" (either normalization) that name a
  merged payee are rewritten to the survivor's name; contains, starts-with and pattern conditions are text
  patterns, not references, and are left alone. A plain rename rewrites the same two kinds, and a rename
  onto an existing name now uses the merge (it used to fail on scheduled transactions and recurring items).
- **Recurring items** are repointed as they are; if the survivor already had an item for the same account
  both stay (detection updates both; the user can dismiss one).
- The learner follows through the audit log (ADR 0027); a test compares the incremental model with a full
  retrain after a merge and its undo.
- UI: Settings → Payees gets a check box per row and **Merge into…**; the dialog picks the survivor (the
  payee with most transactions first) and shows the counts from the preview before merging.

## Consequences

- A stored file can outlive its last reference by up to 30 days; the folder is not a live mirror of the rows.
- Opening an attachment leaves read-only copies in the temp folder (the OS cleans it).
