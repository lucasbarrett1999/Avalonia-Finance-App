# 3. Split-sum rule enforced by triggers and a deferred constraint

- Status: Accepted
- Date: 2026-09-24
- Milestone: M0

## Context

PRD 6.2 requires "sum(splits) == parent amount" as a database **check constraint** on
`TransactionSplit`. SQLite `CHECK` constraints cannot contain subqueries, so the rule cannot be
written as a `CHECK`. A per-row trigger that raises immediately is also wrong: a split set is
written one row at a time, so the sum is necessarily unbalanced between the first and the last
insert of a single save.

## Decision

The initial migration creates:

- `SplitSumGuard`, a table that is always empty, and
- `SplitSumViolations(TransactionId PRIMARY KEY, Guard REFERENCES SplitSumGuard(Id)
  DEFERRABLE INITIALLY DEFERRED)`.

`AFTER INSERT/UPDATE/DELETE` triggers on `TransactionSplits`, and `AFTER UPDATE OF Amount,
CategoryId` on `Transactions`, recompute the affected parent: if it has splits and either the
sum differs from `Amount` or the parent still has a `CategoryId`, a row is recorded in
`SplitSumViolations`; otherwise the row is removed. Because the foreign key is deferred, any
violation still present when the transaction commits fails the commit with
`FOREIGN KEY constraint failed`, and EF Core rolls the save back. Intermediate states inside one
`SaveChanges` are allowed, exactly like a deferred check constraint.

The rule also lives in the domain (`Transaction.SplitsBalance`) so services can reject bad
input with a friendly message before the database does.

## Consequences

- The invariant is enforced by the database for every writer, including raw SQL, as the PRD
  intends. Tests in `Keel.Infrastructure.Tests` cover accept, reject, parent-only edits, and
  cascade deletes.
- Foreign keys must be on (they are, on every connection) for the guard to work.
- EF Core does not model these objects. A future migration that rebuilds the `Transactions` or
  `TransactionSplits` table (SQLite table rebuilds drop triggers) must recreate the triggers;
  the migration test `Model_has_no_changes_missing_from_migrations` does not catch this, so
  such migrations need a test that re-runs the split-sum tests against the migrated schema.
