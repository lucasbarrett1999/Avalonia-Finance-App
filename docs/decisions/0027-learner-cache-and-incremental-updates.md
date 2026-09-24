# 27. Learner cache in the budget file, kept current by replaying the audit log

- Status: Accepted
- Date: 2026-09-24
- Milestone: M4

## Context

F-TXN-5 asks for a learner "retrained incrementally on each approval". ADR 0021 made
`LearnerModel.WithExample`/`WithoutExample` exact (the same JSON as a retrain) and suggested caching
the model in the `Setting` table. The model must follow every change that affects an example:
approvals, category changes, edits of amount, date, account or payee, splits, deletes, payee
renames, and undo and redo of all of them, from any screen. Hooking each call site would miss
paths (the register, undo, the import pipeline being built in parallel).

## Decision

- `LearnerService` (`ILearnerService`) keeps one model per open file. The cache is the setting
  `categorization.learner`: `{format: "keel.learnerCache", version, versionKey, normalizerVersion,
  modelVersion, auditWatermark, model}` where `model` is `LearnerModel.ToJson()` verbatim and
  `versionKey` combines `PayeeNormalizer.Version`, `LearnerModelJson.CurrentVersion` and the cache
  version. A different key, an unreadable model, or a watermark beyond the audit log rebuilds.
- **Build.** On first use: one read transaction (deferred, so writers are not blocked) reads the
  highest `AuditEvent.Id` and the eligible history: approved, categorized, unsplit, non-transfer,
  non-system, not deleted. Hidden and system categories are marked restricted in the catalog.
- **Incremental update.** Every ledger write is audited with before/after row JSON. On each use,
  the service reads the audit events after its watermark for `Transaction`, `TransactionSplit`,
  `Payee` and `Category`. For every touched transaction (its row, its splits, or its payee's name)
  it reconstructs the example the model holds (the "before" of the first later event, the payee name
  before the first rename, split existence at the watermark), removes it with `WithoutExample`, and
  adds the current example with `WithExample`. Category events refresh the catalog. Replay is
  O(changes), not O(history); more than 20,000 pending events, or an example the model does not
  know, rebuild instead. Tests assert the incremental model's JSON equals a full retrain after
  approvals, recategorization, edits, splits, deletes, undo and payee renames.
- **Safety net.** When a cached model is loaded, its example count is compared with a `COUNT` of
  eligible rows; a mismatch (writes that bypassed the audit log, such as fixture generation) rebuilds.
- The cache is derived data: it is written directly (not audited, not undoable), outside the
  service's lock, and never from `PeekModelAsync` (used by import hooks inside a ledger write). All work runs on the thread pool; `Status`
  (`NotLoaded`/`Preparing`/`Ready`/`Failed`) drives the review screen's "Preparing suggestions" state.
  If the learner fails, categorization continues with rules and payee defaults.

## Consequences

- Correctness depends on "every ledger mutation goes through `LedgerWriter`", already a convention.
- A 100k-transaction history trains in well under a second off-thread; a cache hit costs one
  setting read, one count and the replay of recent changes.
