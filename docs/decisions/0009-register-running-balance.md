# 5. Register running balance is the ledger balance in (Date, Id) order

- Status: Accepted
- Date: 2026-09-24
- Milestone: M1

## Context

F-ACC-2 requires the running balance column to be "correct after any edit, sort, or filter (it
is computed from the DB for the visible ordering, not from the in-memory list)". A cumulative sum
taken along an arbitrary visible order (for example sorted by payee, or over a search result) is
not a balance of anything, and it would change meaning every time the user sorts or filters.

## Decision

- Each row's running balance is the balance of its account immediately after that transaction in
  **ledger order**: by `Date`, then by the time-ordered `Id` (entry order within a day), over all
  non-deleted transactions of the account. The All Accounts register uses the same definition
  over all accounts.
- It is computed in SQL for the rows of the requested page only (`RegisterQuery`): one prefix sum
  up to the page's earliest row plus a window sum (`SUM(Amount) OVER (ORDER BY Date, Id)`) up to
  its latest row, both served by covering indexes added in the `RegisterIndexes` migration.
- Sorting and filtering change which rows are shown and in what order, never a row's value.
  Sorted by date (the default), the column reads as a conventional running balance.

## Consequences

- The value is always true and stable: after any edit, sort or filter it matches the account
  balance as of that transaction. `RunningBalance.Compute` in the domain is the reference and
  tests compare every page against it.
- A page sorted by a non-date column may span the whole ledger, so its window sum reads more rows
  (about 290 ms for the first payee-sorted page at 100k, versus about 85 ms for date order).
