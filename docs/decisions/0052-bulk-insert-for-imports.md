# 52. Imports insert new rows through one prepared command

- Status: Accepted
- Date: 2026-09-24
- Milestone: M3

## Context

PRD 11 requires importing 10,000 rows in under 5 s. Inserting 10,000 transactions plus their
10,000 audit rows through EF Core's change tracker took about 6 to 9 s in the sandbox: the SQLite
provider executes one command per row and the tracker snapshots every entity twice. The M1
convention says ledger writes mutate tracked entities (no raw SQL) because undo replays recorded
row states.

## Decision

- Inside the import's `LedgerWriter` unit of work, tracked changes (new payees, matched and
  updated rows, transfer partners, the balance snapshot) are saved first through
  `LedgerSession.SaveAsync`. The new transactions are then written by `BulkLedgerInsert`: one
  prepared `INSERT` reused for every row and one for their `AuditEvent`s, in the same database
  transaction.
- Column names, value converters (ISO dates, enum names, UTC timestamps, upper-case Guid text)
  and parameter types come from the EF model's relational type mappings, never hand-written. The
  row snapshots and audit JSON are built exactly as `EntityChange.Capture` builds them, and are
  handed to the session (`LedgerSession.AddWrittenChanges`), so the undo entry, the audit trail
  and the `LedgerChanged` message are the same as for a tracked insert.
- The convention in CLAUDE.md is amended: raw SQL writes are allowed only through
  `BulkLedgerInsert`, which records row states.

## Consequences

- 10,000 rows now import in about 1.5 to 2.5 s (parse plus pipeline). A test compares the stored
  text of a bulk-inserted row with an EF-inserted one, and the register reads both the same.
- Only inserts of new rows with client keys go through the bulk path; updates stay tracked.
- Undo of a large import deletes rows through EF and takes longer than the import (a few seconds
  for 10,000 rows); it is not on a PRD performance path.
