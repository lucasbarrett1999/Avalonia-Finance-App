# 51. Per-account import memory lives in the Setting table

- Status: Accepted
- Date: 2026-09-24
- Milestone: M3

## Context

F-TXN-2 asks the CSV mapping dialog to remember mappings per account, and the import dialog
should open in the folder the account's last file came from. Options: a new table with a
migration, or the existing key/value `Setting` table (PRD 6.2) for data-file settings.

## Decision

- `IImportSettingsStore` (`Keel.Infrastructure/Import/ImportSettingsStore`) stores JSON values in
  `Setting` under `import.csvMapping.{accountId}` and `import.lastFolder.{accountId}` (lower-case
  "D" Guid). No migration.
- The mapping is stored with the header row it was made for (`RememberedCsvMapping`). It is
  offered for a file only when the headers match (same count, same names ignoring case and
  spaces); another layout gets auto-detection instead of a wrong remembered mapping.
- These are preferences, not ledger data: they are saved directly (not through `LedgerWriter`),
  not audited, and an undo of the import does not forget them. An unreadable value reads as
  "nothing remembered".
- The folder is a machine path kept in the budget file; on another machine it simply does not
  exist and the picker falls back to its default location.

## Consequences

- The memory travels with the budget file (a copied file keeps its mappings).
- Deleting an account leaves two small orphan rows; accounts are closed, not deleted, in v1.
