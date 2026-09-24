# 6. Budget service, targets and quick assign

- Status: Accepted
- Date: 2026-09-24
- Milestone: M2

## Context

F-BUD-3, F-BUD-4 and F-BUD-5 describe move money, targets, "Fund targets" and quick-assign
actions in one or two sentences each. The M0 `IBudgetService` stub fixed some signatures. This
record states how the open points were resolved in `TargetCalculator`, `QuickAssign`
(`src/Keel.Domain/Budgeting/`) and `BudgetService` (`src/Keel.Infrastructure/Budgeting/`).

## Decision

**Targets (F-BUD-4).** All targets are monthly; `Target.Cadence` is stored as `Monthly` (null
for by-date targets) and not otherwise interpreted in v1.

| Type | Needed this month | Underfunded |
|---|---|---|
| Monthly set-aside X | X | max(0, X − Assigned) |
| Debt payment X (linked loan/credit account required) | X | max(0, X − Assigned) |
| Monthly spending X ("refill to X") | max(0, X − Carry) | max(0, X − (Carry + Assigned)); spending this month does not raise the need |
| Savings balance X by date D | ⌈max(0, X − (Available − Assigned)) / n⌉, n = months from this month through D's month (≥ 1; 1 when D is past) | max(0, Needed − Assigned) |

For by-date targets the balance before this month's assignment (`Available − Assigned`, i.e.
carry plus this month's activity) is used, so spending from the fund raises the need; rounding
up means the goal is met on time and the last month needs slightly less. The monthly need is
reported for the Goals screen. A target amount must be positive.

**Fund targets.** Categories are funded in budget display order (group order, then category
order), visible categories only, each with its full `Underfunded` amount until Ready to Assign
(`max(0, RTA(M))`) runs out; the category where it runs out is funded partially. The result
reports the amount funded, the shortfall (the "not enough" message) and the number of
categories funded. One `BudgetChanged` is published when anything changed.

**Quick assign (F-BUD-5).** Each value is a value for Assigned: assigned last month; spent last
month = max(0, −Activity(M−1)); average assigned and average spent over the three previous
months (months without data count as 0; integer division with banker's rounding; spent floored
at 0); fund target = Assigned + Underfunded (absent without a target); reset to zero = 0.

**Move money (F-BUD-3).** Moving X from A to B in month M subtracts X from Assigned(A, M) and
adds X to Assigned(B, M); either side may be Ready to Assign (null), which needs no row. The
amount must be positive and the sides different. Moving more than a category's Available is
allowed (it becomes overspent), as in YNAB. The M0 signature `MoveMoneyAsync(MoveMoneyRequest)`
is kept rather than a four-argument `MoveAsync`.

**Writes.** Setting Assigned to its current value is a no-op (no audit event, no message);
setting it to 0 deletes the row ("absent row means 0"). Inflow categories cannot be assigned or
given targets (`InvalidOperationException`); unknown categories throw `KeyNotFoundException`.
Every changed row gets an `AuditEvent` in the same database transaction:
`EntityType = "BudgetAssignment"`, `EntityId = "{categoryId}/{yyyy-MM-dd}"`, or
`EntityType = "Target"`, `EntityId = "{categoryId}"`, with before/after JSON (enums by name).

**Messages.** Assignment changes publish `BudgetChanged([M])`. Because "assigned in future
months" makes Ready to Assign of every month depend on every assignment, consumers must refresh
Ready to Assign for all months they show, and categories for M and later months. Target changes
publish `BudgetChanged([current month])`.

**Currency.** The budget currency is the currency of the first on-budget account (single base
currency, PRD D3), `USD` when there is none.

**Aggregation SQL.** `BudgetAggregationQuery` uses raw SQL (PRD D6 allows raw SQL on hot paths):
EF Core's LINQ translation grouped through `IX_Transactions_CategoryId_Date` with two `strftime`
calls per row and measured 550 ms at 100k transactions; a `NOT INDEXED` table scan grouped by
`substr(Date, 1, 7)` measured about 170 ms. Raw SQL bypasses the soft-delete query filters, so
each statement filters `IsDeleted = 0` itself; tests pin every rule.

## Consequences

- At 100k transactions one `GetMonthAsync` costs about 250 ms in the sandbox (aggregation
  dominates; the calculator itself takes about 7 ms for 36 months). PRD 11's "budget month
  switch < 100 ms" therefore needs the Budget screen to load a range once (`GetRangeAsync`) and
  switch months in memory, reloading on `BudgetChanged`/`LedgerChanged`. A service-side cache of
  the aggregated activity is a possible follow-up; it was not added because invalidating it
  safely needs a ledger change signal the service cannot observe yet.
- The owner can change any rule above in one place: `TargetCalculator`, `QuickAssign` or
  `BudgetService`.
