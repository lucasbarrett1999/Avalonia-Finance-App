# 50. A stored HasImportMatch flag on transactions

- Status: Accepted
- Date: 2026-09-24
- Milestone: M3

## Context

PRD 6.5 step 4 matches an imported row to a manual or scheduled transaction the user entered
first. ADR 0005 made the matcher consider only manual rows that were never matched before, so one
manual entry absorbs at most one imported row, and asked the pipeline to persist that fact. The
PRD 6.2 `Transaction` has no column for it. Deriving it ("a manual row with a fingerprint") would
break as soon as a manual row gets a fingerprint for another reason, and would be invisible to
anyone reading the file.

## Decision

- Append `bool HasImportMatch` to `Transaction` (migration `ImportMatchFlag`, `INTEGER NOT NULL
  DEFAULT 0`, an `ADD COLUMN`, so no table rebuild and the ADR 0003 triggers stay intact).
- The import pipeline sets it, together with the incoming row's fingerprint and provider id, when
  it applies a `MatchExistingManual` decision; it also marks the matched row cleared (the bank has
  confirmed it) unless the incoming row is pending. The pipeline loads the flag into
  `DedupCandidate.HasImportMatch`. Undo of the import clears it again.
- Nothing else writes it. Editing the manual row later keeps the flag.

## Consequences

- Existing files get `false` for every row, which is correct: no import matches existed before M3.
- Raw-SQL inserts elsewhere (fixture generator, tests) keep working through the column default.
