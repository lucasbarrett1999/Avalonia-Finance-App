# 26. Rule persistence, retroactive apply and where rule changes land

- Status: Accepted
- Date: 2026-09-24
- Milestone: M4

## Context

ADR 0020 defines the rule format and a pure engine that returns a mutation set. Persisting rules and
writing mutations to the ledger leaves questions open: how rules are ordered and audited, what a
retroactive apply may change, how the preview is guaranteed to match the apply, and where the
`flag` action lives, since `Transaction` has no flag column.

## Decision

**Persistence** (`IRuleService`, `RuleService`). Rules are stored with `RuleDefinition.ToEntity()`
(versioned `RuleJson`) and read with `RuleDefinition.FromEntity`. Every save validates with
`RuleValidator` against the file's categories and accounts and is refused with
`RuleValidationException` (carrying every problem) on errors. Create, edit, enable, move, reorder
and delete each run through `LedgerWriter` as one audited, undoable action (`SaveRule`,
`DeleteRule`, `ReorderRules`) and publish `RulesChanged`. New rules go last. Moves and drag orders
renumber `SortOrder` to 0..n-1. A rule this version cannot read is listed with its format error,
skipped by the engine, and never rewritten.

**One plan for preview and apply.** `MutationPlanner.Plan` turns an engine result into what the
ledger accepts; the preview and the apply call it on the same rows, so they cannot disagree (a test
compares them field by field). The planner keeps the rule order semantics of ADR 0020 and reduces:
- no payee on transfers; no splits on transfers;
- a category on a transfer only on the side `TransferRules.SideRequiresCategory` allows;
- a new transfer only from an unsplit, non-transfer row to another existing, open account, and only
  with a category when the pair of accounts needs one; its counterpart row is created as the ledger
  service would (uncleared, approved, category on the side that needs it). The imported descriptor
  (`PayeeRaw`) is kept.
Everything else (memo, tags, approval, splits replacing a category, a category replacing splits)
is written as the engine produced it. Amount, date and account are never changed by rules, so
reconciled rows are eligible.

**Retroactive apply.** `PreviewRetroactiveAsync(ruleIds, scope)` streams the transactions in the
scope (date range, account, unapproved only; oldest first), applies only the chosen rules in their
stored order, and lists every row that would change with before/after display values.
`ApplyRetroactivelyAsync` recomputes the same plan inside one `ApplyRules` unit of work, so one
undo reverts the whole run (including payees and tags it created). Re-applying is idempotent.

**Flag.** The `flag` action is stored as the reserved tag `Flagged` (`TransactionTags`), which the
register search already understands (`tag:flagged`). Adding a column would need a migration while
other branches change the schema; a later migration can move it to a column and convert the tags.

**Payee management (F-TXN-9, partial).** `IPayeeService` gains `ListAsync`, `SetDefaultCategoryAsync`
and `RenameAsync` (`UpdatePayee` action). A rename changes the payee row, so every transaction shows
the new name. Renaming to a name another payee already has moves this payee's transactions to that
payee and removes this one (keeping a default category when the target has none): the closest
reading of "rename with retroactive application" when names are unique. Payee merge as its own
command stays P1.

## Consequences

- `CountMatchesAsync` and `TestAsync` scan all transactions on demand (streamed, off the UI thread).
- The M1 rule "every mutation through `LedgerWriter`" holds for rules and payees; undo replays
  rule rows like any other row.
