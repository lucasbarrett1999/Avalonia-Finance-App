# 25. Rules and the learner in the import pipeline

- Status: Accepted
- Date: 2026-09-24
- Milestone: M4

## Context

F-TXN-1 steps 3 and 4 run payee rename rules, categorization rules, and then the learner on every
imported batch. The M3 pipeline, built in parallel, exposes `IImportCategorizationHook`: every
registered hook sees the rows about to be inserted as `ImportDraft`s (payee name, category, memo,
approval, reason), during previews too, inside the import's unit of work, and must not write.
When this work started the seam was not on `main`; this branch first shipped an
`ImportCategorizationAdapter` over already-inserted ids and replaced it once the seam landed.

## Decision

- `RulesImportCategorizationHook` is registered after the no-op default (all hooks run in order).
- **Step 3** (`RenamePayeesAsync`): the stored rules run on each draft (current payee name and raw
  descriptor, amount, date, account, source, memo); a rule's `setPayee` becomes the draft's payee
  name, which the pipeline then resolves or creates and whose default category it fills in.
- **Step 4** (`CategorizeAsync`): `ICategorizationEngine` runs with the rules, the payee default
  (a category the pipeline filled in when the source gave none) at 0.95, and the learner model.
  A rule's category, the payee default, or the learner's primary suggestion (at least 0.60) is
  written to the draft with the trace summary as `CategoryReason`; a rule's memo changes and
  "mark approved" apply too. Rows stay unapproved otherwise.
- **No writes.** The hook reads rules and categories through its own short-lived context and the
  model through `ILearnerService.PeekModelAsync`, which never writes the cache. `LearnerService`
  writes its cache outside its lock, so a concurrent review screen never makes an import wait.
- Rule actions a draft cannot express (tags, flag, splits, transfers) are not applied at import;
  they apply when the review queue approves with the rule or when rules are applied retroactively.
- `ICategorizationService.CategorizeAsync(ids)` remains for categorizing already-stored rows with
  the same order (rules write, then payee default or learner).

## Consequences

- Preview and import show the same categories, because the pipeline's planner calls the hook for both.
- Import cost grows by one rule compile, one learner lookup (cached after the first) and one engine
  run per new row.
