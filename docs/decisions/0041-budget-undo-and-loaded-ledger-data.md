# 41. Budget actions on the undo stack, and recomputing over loaded ledger data

- Status: Accepted
- Date: 2026-09-24
- Milestone: M2 (Budget screen)

## Context

F-BUD-3 requires moving money to be undoable, and PRD 11 requires "undo for every user action".
The M2 engine's `BudgetService` audited its writes but did not join the session undo stack that
M1's `LedgerWriter`/`UndoService` keep for ledger actions. Separately, PRD 11 asks for a budget
month switch under 100 ms and F-BUD-2 for assignments to update Ready to Assign "within one
render frame", while one `GetMonthAsync` costs about 250 ms at 100k transactions because the
activity aggregation scans the transaction table (ADR 0008).

## Decision

**Undo.** `BudgetService` takes the `UndoHistory` (an optional constructor parameter, so the
existing tests that construct it directly are unchanged). Assign, move money, fund targets, set
target and delete target capture the tracked `BudgetAssignment` and `Target` rows they change as
`EntityChange`s just before saving and record one undo entry per call, with new `LedgerAction`
values (`AssignBudget`, `MoveMoney`, `FundTargets`, `SetTarget`, `DeleteTarget`). Their own audit
events (ADR 0008 format) are unchanged. `UndoService` replays these entries like ledger ones;
the replay is audited by `LedgerSession` in its generic format (`EntityType = "BudgetAssignment"`,
key `"{categoryId}|{yyyy-MM-dd}"`). `LedgerWriter` now publishes `BudgetChanged` (assignment
months; the current month for target rows) when a unit of work changed budget rows, and skips
`LedgerChanged` when it changed nothing else, so undoing an assignment refreshes the budget and not
every register.

**Month notes** (F-BUD-7) are `Setting` rows keyed `budget.monthNote.yyyy-MM` (a JSON string),
written by `IBudgetService.SetMonthNoteAsync` with an audit event and an undo entry
(`EditMonthNote`); `LedgerWriter` treats them as budget rows too. No schema change was needed.

**Loaded ledger data.** `IBudgetService` gains (append-only) `LoadLedgerAsync(from, to)`, which
returns the ledger-derived calculator input (accounts, groups, categories, aggregated activity,
card payments) and the card balances of the range as an opaque `BudgetLedgerData`, and overloads of
`GetRangeAsync`, `ExplainAsync` and `GetQuickAssignAsync` that take it and read only assignments
and targets. The Budget screen loads 12 months back and 3 ahead once, recomputes after
`BudgetChanged` (assignments, targets) without touching transactions, reloads after
`LedgerChanged`, and switches months in memory. All budget math stays in `BudgetCalculator`.

Alternatives rejected: a service-side cache of the aggregation invalidated by a change counter
(writes outside `LedgerWriter`, e.g. tests and fixtures, would make it stale silently); optimistic
arithmetic in the view model (carry flooring, overspending and card coverage make it wrong).

## Consequences

- Every budget action is undoable from the shell, the budget header and the status toast.
- Budget writes do not take `LedgerWriter`'s write gate; the Budget screen serializes its own
  writes, so the undo order follows the user's order. Concurrent writes from elsewhere could
  interleave entries, which only changes which one is undone first.
- A screen holding `BudgetLedgerData` must reload it after `LedgerChanged`; the Budget screen does
  so lazily when it is not visible.
