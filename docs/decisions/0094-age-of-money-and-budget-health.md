# 94. Age of money and budget health

- Status: Accepted
- Date: 2026-09-25
- Milestone: M9 (stream B)

## Context

F-REP-5 lists age of money ("days between inflow and outflow, YNAB definition"), a months-ahead
metric, the percentage of targets funded and an overspending count, without formulas. Reports must be
aggregated in SQL (ADR 0060, principle 1) and budget numbers must come only from `BudgetCalculator`
(PRD 6.4, CLAUDE.md).

## Decision

**Age of money** (`Keel.Domain/Reports/AgeOfMoney`, fed by one raw-SQL `GROUP BY` day in
`ReportService.GetAgeOfMoneyAsync`).

1. Money counts on **on-budget cash accounts** (checking, savings, cash). A day's *inflow* is the sum of
   positive amounts, its *outflow* the sum of negative amounts (as a positive number), and the number of
   outflow transactions is counted. Non-deleted rows of every source count, starting balances and
   reconciliation adjustments included (a starting balance is money you had).
2. **Transfers between two on-budget cash accounts** (and split lines that are such transfers) are left
   out: they move money without receiving or spending it. Card spending is not an outflow until the card
   is paid: a payment to a credit account is an outflow of the cash account (YNAB's rule), as are
   transfers to tracking accounts. A cash advance from a card is an inflow.
3. **First in, first out.** Inflows queue by date; each outflow spends the oldest money left. A day's
   inflows are available to its own outflows (age 0) once older money is used up. All outflows of a day
   share the day's amount-weighted age (the SQL aggregates per day, not per row).
4. **Age of money** at a date = the average age of the **last 10 outflows** on or before it (a busy day
   at the edge of the window contributes only the outflows needed), rounded half to even to whole days.
5. **Unfunded spending** (outflows beyond all money received so far, an overdrawn budget) has no age: it
   is left out of the average, and the next inflows repay that shortfall before they queue.
6. The history is the age at every month end of the range (the current month's point is today); the
   Budget health report skips leading months without an age, and Home shows the last twelve months.

**Budget health** (`ReportService.GetBudgetHealthAsync(month)`; the range's last month, at most this
month; "as of" is the month's last day or today).

- **Months ahead** = (Ready to Assign + every positive Available outside credit card payment
  categories) ÷ average monthly spending, rounded down to a tenth. Average spending is the Spending
  report's total (default toggles: on-budget accounts, categorized transfers counted, system rows and
  inflow categories not) over the **three complete months before the month**, from the first month with
  any on-budget activity; with no complete month there is no figure. Card payment categories hold money
  already spent on the card, so they are not a buffer.
- **Targets funded** = Σ (needed this month − underfunded) ÷ Σ needed this month over visible
  categories with a target, whole percent half to even; 100% when targets exist but need nothing this
  month; no figure without targets. The count "k of n targets funded" uses `Underfunded = 0`.
- **Overspending count** = visible categories whose Available is negative this month, split into cash
  (red, reduces next month's Ready to Assign) and credit-only (yellow), most overspent first.
- The month's budget numbers come from `IBudgetService.GetMonthAsync` (so from `BudgetCalculator`),
  which `ReportService` receives as an optional dependency; only spending and age of money are SQL.
  Accounts filter and transfer/tracking toggles do not apply (the report is budget-wide).

The report shows each metric with the numbers behind it and a "How these are calculated" card; history
points and overspent rows drill to the register (the month's transactions; the category's month).

## Consequences

- The per-day aggregation makes age of money a single `GROUP BY` (about 200 ms over the 100k fixture's
  whole history); per-transaction ages within one day are not distinguished, which only matters when a
  single day's outflows span several inflows.
- Months ahead is a buffer measure ("how long could the money already budgeted last"), not YNAB 4's
  retired "budgeted N months ahead" figure.
