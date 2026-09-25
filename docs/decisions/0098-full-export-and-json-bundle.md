# 98. Full export and the lossless JSON bundle

- Status: Accepted
- Date: 2026-09-25
- Milestone: M9 (stream D)

## Context

F-REP-6 (P1) asks for a full export to CSV (transactions, budget, accounts, categories, rules) and a JSON
bundle that re-imports into a fresh Keel database losslessly. The ledger can hold 100k+ transactions, so
neither side may need the whole file in memory. Other M9 streams add tables and columns while this one is
built, so a hand-written table list would go stale.

## Decision

- **CSV export** (`DataExportService.ExportCsvAsync`): nine files (`CsvExportFiles`: transactions, budget,
  accounts, categories, payees, rules, targets, scheduled transactions, recurring items) into a folder (which
  must not already hold an export) or a zip. Stable column order defined in `CsvTables`; ISO dates, months as
  `yyyy-MM`; amounts as invariant decimals with the currency's minor digits (`-12.30`, JPY `-1230`) in the
  account's currency (budget and targets use the budget currency, the most common on-budget currency);
  booleans `true`/`false`; ids lower-case "D"; UTF-8 without BOM; `\n` line ends. Split transactions are
  flattened to one line per split with the parent id in `Parent Id` (the lines sum to the transaction, and the
  parent is not repeated as its own line). Tags are joined with ", ". Deleted transactions are left out.
- **One snapshot, paged**: each export runs in one deferred SQLite read transaction (a consistent WAL
  snapshot that never blocks writers). Transactions are read in keyset pages of 2,000 by (Date, Id) with their
  splits and tags per page; other tables stream row by row. Files are written as `.partial` and renamed.
- **Bundle format** `keel-export` version 1: one JSON object with `format` (first), `version`, `exportedAt`,
  `app`, `sourceFile`, `schema` (newest migration), `counts` per table, `attachmentFiles`, then `tables`
  (each `entity`, `table`, `columns`, `rows` as arrays in column order) and `attachments` (relative path and
  base64 content of each file in the attachments folder). Values: Guid "D", `DateOnly` ISO, `DateTime` ISO 8601
  UTC round-trip, enums by name, numbers as JSON numbers (doubles in shortest round-trip form).
- **Model-driven tables** (`BundleSchema`): every EF entity type except `AuditEvent`, in foreign-key order,
  every mapped property, rows ordered by key, soft-deleted transactions included. A table or column added by a
  later migration is carried without code changes. Left out: the audit log (the undo and activity history of
  the source file) and two `Setting` rows that record positions in that audit log (`categorization.learner`,
  the learner cache, and `recurring.importAuditWatermark`); the new file rebuilds both.
- **Import** (`BundleImportService.ImportIntoNewFileAsync`): only into a new file or an empty migrated one
  (no accounts, transactions, user categories, payees, rules, tags, assignments or schedules); anything else
  is `BundleError.TargetNotEmpty` and the file is not touched. The bundle is streamed token by token
  (`JsonTokenStream`); each table is written by `BulkTableWriter`: one prepared `INSERT ... ON CONFLICT DO UPDATE`
  per table (the conflict updates the rows every new file is seeded with: default profile, Inflow and Credit
  Card Payments groups, Ready to Assign) plus one `AuditEvent` per row built like `EntityChange` (ADR 0052
  style); column names and conversions come from the EF model. All in one transaction with
  `PRAGMA defer_foreign_keys = ON`; before the commit the split-sum violation table must be empty and
  `PRAGMA foreign_key_check` must return nothing, and every table's row count must equal the header's.
  A failure rolls back and deletes the file the import created. Attachments are staged and moved next to the
  new file after the commit. The import is not an undoable action (the file is new); the caller opens it in a
  new session (ADR 0080).
- **Versioning**: a newer `version`, a `schema` newer than this build's migrations, an unknown table, column or
  enum value are refused as `BundleError.TooNew` ("update Keel"), never imported partially. Columns missing from
  an older bundle take the entity's defaults. Not a bundle, truncated or damaged is `NotABundle`; broken rules
  (missing referenced row, splits that do not add up) are `Invalid`.

## Consequences

- The round-trip test compares the stored values of every table (row counts and a SHA-256 in key order)
  independently of the export code; the 100k fixture round-trips in about 2.3 s export and 9 s import
  (sandbox). Import time is dominated by writing the audit rows; a faster path without them would break the
  "records audit rows" rule of ADR 0052.
- Bundles are about 300 bytes per transaction (31 MB for the 100k fixture). They are not encrypted; the user
  guide says so, as it does for backups.
- The learner rebuilds from history the first time the restored file needs it.
