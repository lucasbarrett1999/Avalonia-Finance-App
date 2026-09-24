# 32. Recurring alerts: exact rules and idempotency keys

- Status: Accepted
- Date: 2026-09-24
- Milestone: M5

## Context

F-REC-3 lists four alert kinds in one line each, and PRD 6.6 adds the price-increase threshold
("exceeds the previous by > 5% and > $1"). The `Alert` entity (PRD 6.2) has no column that
identifies the occurrence an alert is about, yet alerts must not repeat every time detection
or import runs.

## Decision

`Keel.Domain.Alerts.AlertEvaluator` is pure. Input: today's date, stored items (with their
normalized payees, as they were before the batch), relevant history, the new batch, the
reconciliation decisions of the accompanying detection run, and the keys of all stored alerts
(including read and dismissed ones). Output: proposals.

1. **Idempotency.** Every proposal has a key naming (kind, item, occurrence):
   `PriceIncrease:{item}:{transaction}`, `MissingExpected:{item}:{expected date}`,
   `NewRecurring:{item}`, `TrialConversion:{transaction}`. The key is stored as `key` in
   `Alert.PayloadJson` (camelCase JSON, nulls omitted, enums by name) and read back with
   `AlertKeys.Of(alert)`, so no schema change is needed. A key already stored, or proposed
   earlier in the same run, is never proposed again. A dismissed alert therefore stays
   dismissed. A row that appears in both history and batch counts once.
2. **Price increase.** For each batch outflow that belongs to an Active or Detected item with a
   fixed amount (same payee; same account, or the item has no account): compare with the
   previous outflow of the same payee and account (history and batch together, ordered by date
   then id), or with the item's expected amount when there is none. Alert when the magnitude
   grew by more than $1 **and** by more than 5% (both strict). Only outflows: a larger paycheck
   is not a price increase. **Variable-amount items (utilities) are skipped**, since their
   month-to-month swings would alert constantly. The payload carries both amounts and the
   increase in basis points (integer division).
3. **Missing.** Only Active (confirmed) items: an unconfirmed detection may be a coincidence.
   Missing when today is at least 3 days after `NextExpectedDate` and no transaction of the item
   (same direction) dated from the cadence's early tolerance before the expected date (weekly 2,
   biweekly 3, semimonthly 2, monthly 5, quarterly 10, yearly 20 days), and after the last seen
   date, up to today has arrived. One alert per expected date.
4. **New recurring item.** One alert per `Create` decision of a detection run (not for updates,
   re-enabled or resumed items).
5. **Trial conversion.** "First charge after a $0/trial charge from the same payee": a batch
   outflow of more than $1 whose previous outflow-or-zero row from the same payee (any account;
   refunds ignored) was $0 or at most $1 and at most 90 days earlier. "Trial charge" includes
   $1 authorizations, which several services use instead of $0. The 90-day limit keeps a
   years-old $0 authorization from turning an unrelated later charge into an alert. The
   alert links the payee's item if one exists (any status but Dismissed).
6. Proposals are ordered by kind, then date, then key.

## Consequences

- The alert service must pass the keys of every stored alert, not only unread ones.
- JTBD5 asks to know "before it happens"; with transaction data alone the evaluator can only
  report the first converted or increased charge. Warning ahead of a trial's end needs the trial
  length, which Keel does not know.
