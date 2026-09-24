# 28. Review queue: what each triage key writes

- Status: Accepted
- Date: 2026-09-24
- Milestone: M4

## Context

F-TXN-6 lists the review keys (`A` approve, `1–9` pick from top suggestions, `C` change category,
`S` split, `T` mark transfer, `R` create rule, `D` delete, `J/K` move) and a batch "approve all with
confidence ≥ 90%", and PRD 9.5 describes a single-focus list with progress "23 of 61". It does not say
whether picking a category also approves, what `A` does with an uncategorized row, how progress
counts when approved rows leave the queue, or what the batch approves when a row already has a
category.

## Decision

- **Every decision approves and advances.** `A`, `1–9`, `C`, `S` and `T` record the decision and
  approve the transaction in one undoable action; the next transaction moves into the focus. `J/K`
  (and the arrow keys) only move. `R` opens the rule editor prefilled by `RuleSuggester`; after saving,
  the focused transaction is rescored, so the new rule's category is suggestion 1. `D` soft-deletes
  with an Undo toast.
- **`A`** keeps the transaction's category when it has one (or is split or a transfer); an
  uncategorized row takes the primary suggestion (rule, payee default at 95%, or the learner at 60% or
  more). A decision taken from a rule suggestion also writes that rule's other changes (rename, memo,
  tags, flag, splits) through the same plan as retroactive apply (ADR 0026).
- **Suggestions** are computed as if the row had no category, so rows the importer already
  categorized still show why, with the trace behind "Why?" (ADR 0022). The screen shows up to five.
- **Batch.** "Approve N with confidence ≥ 90%" covers every unapproved transaction (not just the
  loaded page) whose primary suggestion reaches 90% and does not contradict a category the row
  already has; it writes that category and approves, as one undoable action.
- **Progress.** "x of y" counts the transactions decided in this visit plus those still waiting:
  x = decided + focused position + 1, y = decided + remaining. The bar shows decided / y.
- **Paging.** The queue reads 50 rows at a time through `IRegisterQuery` (unapproved, all accounts,
  oldest first); the focus keeps its transaction across refreshes when it is still unapproved.
- **Split and transfer** save through `ITransactionService.SaveAsync`, which (since M1) replaces the
  stored descriptor with the payee name when a row is edited; rules keyed on the raw descriptor match
  the payee name afterwards.

## Consequences

- One key per transaction is enough for the common case, and every decision is undoable.
- The learner learns from each approval through the audit-log replay (ADR 0027), with no extra call.
