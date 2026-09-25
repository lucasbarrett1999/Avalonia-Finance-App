# 81. Backups, restore and data-file care

- Status: Accepted
- Date: 2026-09-25
- Milestone: M8

## Context

F-SET-1 asks for Backup now (a timestamped zip of the database and attachments), automatic daily
backups keeping N, and restore with confirmation. PRD 10 adds verification by reopening the copy, a
daily `PRAGMA integrity_check` and a diagnostic bundle without rows; PRD 11 asks for a backup before
every migration.

## Decision

- **Where and how.** Backups go to `<datadir>/budgets/backups/` (PRD 7.4) as
  `Name-YYYYMMDD-HHMMSS[-auto|-before-migration|-before-restore].zip` (local time). A zip holds
  `backup.json` (format 1, file name, kind, UTC time, last migration), the database as `Name.keel`
  and the attachments folder. The database copy is made with the SQLite backup API (consistent while
  the app runs) and switched to rollback-journal mode so it is one file.
- **Verification** extracts the database to a scratch folder and requires the manifest, `integrity_check`
  = ok, a Keel schema with no unknown migrations, and the ledger tables. A copy that fails is deleted
  and the action reports the error. Restore refuses damaged, foreign or newer backups.
- **Kinds and keep-N.** Only automatic backups are pruned by keep-N (default 10, 1–365, in
  settings.json with the on/off switch). Manual, before-migration and before-restore backups are kept
  until the user deletes them. The daily run happens once per app day, 20 s after the session starts,
  and never over a file whose integrity check failed that day.
- **Restore** takes a before-restore backup, copies the backed-up database over the open file with the
  SQLite backup API (so open connections see the new content), replaces the attachments folder, and
  reopens the file in a new session (ADR 0080). It is not an undoable action; the before-restore
  backup is the way back, and the confirmation says so.
- **Integrity check** runs on the first session of each local day per file; the day is stored in the
  file's `Setting` table (`maintenance.integrityCheckedOn`) so it travels with the file. A failure is
  an error in the status strip with the next step (restore a backup); the check can also be run from
  Settings → General.
- **Change location** copies the file (backup API) and attachments to the new place, verifies the
  copy, opens it in a new session, then deletes the old file, its WAL/SHM files and attachments. If
  deleting fails the old copy is kept and the status strip says where.
- **Diagnostic bundle**: `keel-diagnostics-YYYYMMDD-HHMMSS.zip` in `<datadir>/diagnostics` with the
  log files and `schema-summary.json` (app version, OS, SQLite pragmas, migrations, and for each table
  its columns, indexes and row count). No row values are read. The path is copied to the clipboard.
- **Rollback-journal files open normally.** The pragma interceptor no longer sets `journal_mode` on
  read-only connections (EF Core's existence check opens one), which failed with "attempt to write a
  readonly database" on copies in rollback-journal mode; the first read-write connection switches the
  file to WAL.

## Consequences

- Backups of two files with the same name in different folders share one folder; the list shows the
  backups whose name starts with the open file's name.
- Backups live on the same disk as the data directory; the user guide says to copy them elsewhere.
