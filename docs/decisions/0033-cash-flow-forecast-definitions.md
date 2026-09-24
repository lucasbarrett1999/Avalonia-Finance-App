# 33. Cash-flow forecast: horizon, sources, double counting and discretionary spend

- Status: Accepted
- Date: 2026-09-24
- Milestone: M5

## Context

F-REP-4 names the inputs of the forecast (current cleared balance, scheduled transactions,
confirmed recurring items, optional average daily discretionary spend of the last 90 days) and
the outputs (daily balance per on-budget cash account for 90 days, lowest projected balance,
days below a user floor). It leaves open where the series starts, what happens to occurrences
that are already overdue, how to avoid counting a bill twice when it is both scheduled and
detected, and what "discretionary" means.

## Decision

`Keel.Domain.Forecast.ForecastEngine` is pure and every day can be explained
(`ForecastResult.Explain(date, account?)` returns opening balance, entries and closing balance).

1. **Accounts.** Open, on-budget, cash-like accounts (checking, savings, cash; PRD 6.3). Card,
   tracking and closed accounts are not projected. A combined series sums the projected accounts.
2. **Horizon.** Day 0 is today; the series runs through today + N days (N = 90 by default), so it
   has N + 1 points. Day 0 opens at the **cleared** balance (literal F-REP-4) and includes what is
   due today. Uncleared rows are not added: the PRD says cleared balance.
3. **Scheduled transactions.** Occurrences of the rule (ADR 0030) from `NextDate` (the rule's
   anchor defaults to `NextDate`) through the horizon and `EndDate`. A scheduled transfer puts the
   opposite amount on the other account when that account is projected; one recorded on a card
   with a projected cash counterpart contributes only the cash side.
4. **Recurring items.** Only `Active` items (confirmed; `Detected`, `Paused`, `Ended` and
   `Dismissed` are skipped with a reason). Items need an account that is projected. Occurrences
   come from the item's rule (inferred from cadence, next expected date and last seen date, ADR
   0031) starting at `NextExpectedDate`, at the expected amount (the median for variable items).
5. **Double-count guard.** A recurring item is skipped (`CoveredBySchedule`) when a scheduled
   transaction is linked to it (`RecurringItem.ScheduledTransactionId`) or when any scheduled
   transaction has the same payee and the item's account (as its account or transfer account).
   The schedule wins because it is the user's explicit statement.
6. **Overdue occurrences.** An occurrence dated before today that has not happened (a scheduled
   instance not yet entered, a recurring item whose next date passed) lands on day 0, flagged
   overdue with its due date, when it is at most 14 days old (option). Older ones are ignored:
   they are more likely skipped or cancelled, and the missing-item alert reports them.
7. **Discretionary spend (toggle, off by default).** Per account: the sum of outflows dated in
   the 90 days before today (today excluded, since today's activity is partly in the ledger
   already), excluding transfers, rows entered from a scheduled transaction, and rows whose payee
   has a recurring item (any status but Dismissed, on that account or without an account) or a
   scheduled transaction on that account; divided by the days in the window, rounded half to
   even. When the account opened inside the window, the window starts at the opening date.
   Inflows are not averaged: irregular income is not projected. The amount is subtracted on
   every day after day 0. The explanation records the window, total, counted and excluded rows.
8. **Floor and low point.** The lowest projected balance is the day with the lowest closing
   balance (earliest on ties), day 0 included. Days below the floor are those with a closing
   balance strictly below it, listed per account and for the combined series.

## Consequences

- The forecast service (later task) must pass payee ids for scheduled transactions, recurring
  items and history rows, and flag transfers; the engine does not look up payees.
- Because the start is the cleared balance, a user with many uncleared rows sees a forecast
  that is off by those rows until they clear. An "include uncleared" option can be added
  without changing the engine's shape.
