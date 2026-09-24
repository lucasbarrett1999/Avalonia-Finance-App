# 42. Category management rules

- Status: Accepted
- Date: 2026-09-24
- Milestone: M2 (Budget screen)

## Context

F-BUD-1: groups and categories are "freely created, renamed, reordered, hidden, and deleted
(deleting a category with history requires re-assigning its transactions to another category)",
and system groups exist. The PRD does not say what "history" is, what happens to assigned money,
targets, schedules and payee defaults, or which system rows can change.

## Decision

- `ICategoryService` gains (append-only) group and category management, notes (F-BUD-7) and
  starter templates (F-BUD-8). Every change is one `LedgerWriter` action: audited, undoable,
  announced with `LedgerChanged`.
- **Protected rows.** The Inflow group, Ready to Assign, the Credit Card Payments group and its
  per-card categories cannot be renamed, hidden, moved, deleted or receive new categories (card
  payment categories follow their account's name, as in M1). The Credit Card Payments group can be
  reordered among the other groups; Inflow is not listed.
- **History** is any transaction or split line (soft-deleted ones included), any assignment row,
  or any scheduled transaction referring to the category. Deleting a category with history requires
  a replacement: a user category that is not being deleted. Transactions, splits, schedules and
  recurring items move to it, payee default categories follow, and assignments are added to the
  replacement's in the same months (a zero sum removes the row), so Ready to Assign and every
  month's total assigned are unchanged. The deleted category's target is removed. Deleting a
  category without history needs no replacement (payee defaults are cleared).
- **Groups.** Deleting a group deletes its categories with the same rules and one replacement
  (outside the group) for all of them.
- **Hiding** is the existing flag (ADR 0007: hidden categories still count). **Moving** a category
  renumbers the sort order of the groups involved.
- **Templates** create missing groups and categories by case-insensitive name and never delete or
  rename; applying one twice adds nothing.
- Rules' JSON conditions and actions (M4) are not rewritten when a category is deleted; the rules
  milestone owns that.

## Consequences

- Deleting a category is fully reversible with undo, including the moved history.
- A user cannot lose assigned money by deleting a category; it lands in the replacement.
