# 60. Report rules, net-worth snapshots, goal pace and drill-down

- Status: Accepted
- Date: 2026-09-24
- Milestone: M6 (part 1)

## Context

F-REP-1..3, F-ACC-7, F-GOAL-1 and F-DASH-1 describe the reports, goals and dashboard in a few
sentences each. Several rules they depend on are open: what counts as spending and income, what the
"include transfers" toggle means, how a snapshot combines with ledger activity, what "current pace"
is, and how a chart element reaches "the underlying transactions" with the register's existing
filters. This record states the choices made in `ReportService`, `BalanceSeries`,
`GoalProjection`, `GoalService` and the report view models. Every rule has a test in
`ReportServiceTests`, `ReportMathTests`, `GoalServiceTests` or `ReportsGoalsHomeTests`.

## Decision

**Rows that count (spending and income).** Non-deleted transactions without splits and the split
lines of non-deleted transactions (a split parent counts only through its lines), dated in the
range, in the selected accounts (none selected = all).

1. System rows (`Source = System`: starting balances and reconciliation adjustments) never count.
   A starting balance is not income, and a loan's opening balance is not spending.
2. Tracking accounts count only with "Include tracking accounts" (default off for Spending and
   Income vs expense, so they match the budget).
3. Uncategorized transfers (between on-budget accounts, card payments, the tracking side of a
   transfer) never count: they move money, they do not earn or spend it.
4. "Include transfers" (default on) decides whether categorized transfers count: the on-budget
   side of a transfer to a tracking account (for example an investment contribution) is budget
   activity, so with the defaults Spending equals the negated budget Activity of every spending
   category (a cross-check test asserts this on the fixture).
5. Spending is the negated sum per category (refunds reduce it; a category can be negative and
   then appears in the table but not in the donut). Inflow categories (Ready to Assign) are income,
   not spending. Rows without a category form an "Uncategorized" group.
6. Income versus expense classifies each row: Inflow-group category, or uncategorized with a
   positive amount, is income; everything else is expense (negated, so refunds reduce expense).
   Net = income − expense. Months without activity are listed with zeros.
7. The comparison period (F-REP-1) is the same number of whole months immediately before a
   month-aligned range, otherwise the same number of days immediately before.

**Net worth (F-REP-3, F-ACC-7).**

8. Points are the end of every month that intersects the range; the last point is the range end
   or today, whichever is earlier (no future points), and points before the first included
   account's opening date are dropped (they would read as a net worth of zero).
9. On-budget accounts use the ledger balance (the sum of all activity on or before the point);
   provider snapshots on on-budget accounts are ignored (principle 1: the ledger is the truth).
10. A tracking account with snapshots uses the latest snapshot on or before the point **plus
    ledger activity dated after that snapshot** up to the point; before its first snapshot it uses
    the ledger. A snapshot is the end-of-day balance of its date, so activity on that date is
    already in it. Without snapshots a tracking account uses its ledger balance.
11. Balances are signed (liabilities negative). Assets = Σ non-liability balances, Liabilities =
    −Σ liability balances, Net worth = Assets − Liabilities. Closed accounts stay in (F-ACC-1).
    The "include transfers" toggle does not apply to net worth and is hidden there.
12. One base currency (PRD D3): report totals use the currency of the first on-budget account.

**Goals (F-GOAL-1).** A goal is any visible category with a savings-balance-by-date target;
the Goals screen creates new ones in a "Goals" group (created on demand by the category service)
with the target's `LinkedAccountId` holding the optional tracking account, which must be off
budget. Progress = Available / target. "Monthly need" is the target's monthly need from
`TargetCalculator` (ADR 0008). "Current pace" is the average Assigned over the three months ending
with the current month (missing months count as 0, half to even); this month is included so a
goal funded this month shows a pace at once. The projection adds the pace (plus the what-if
amount) each following month: months = ⌈remaining / monthly⌉, capped at 100 years; no
projection when the monthly amount is not positive. Creating a goal is two service calls
(category, then target); if the second fails the category stays and can be removed in Budget.

**Drill-down.** The register's filters are an account, a category and the F-TXN-7 search, so:
a category (slice or row) opens the All Accounts register with that category filter and
`date:from..to` in the search box (visible and editable); a group or "Other" slice drills to the
next level inside the report; the Uncategorized bucket opens the register for the range (the
register has no "uncategorized" filter yet, so the rows are not narrowed further); an income bar
opens Ready to Assign for that month; an **expense bar opens the Spending report for that month**
(its breakdown, from which categories open the register); a net point opens every transaction of
that month; a net-worth point opens the month's transactions and an account band opens that
account's register. When exactly one account is selected in the toolbar, drill-downs open that
account's register. `RegisterNavigation` gained an optional `Category` (and resets the other
register filters when it is set; `CategoryOption.All` clears the category).

**Charts.** LiveCharts 2.0.5 with animations off (renders identically in tests and respects
reduced motion), its built-in legend hidden (each chart has a text legend or table with the
same palette swatches), `DataPointerDown` (the pinned version marks `ChartPointPointerDown`
obsolete). One categorical palette of eight slots (validated for colour-vision deficiency in both
themes); a ninth series folds into "Other" (grey) and is itself drillable. Slots follow rank at
each level; net-worth accounts keep their sidebar order.

**Export.** "Export CSV" writes the selected report's table (RFC 4180, UTF-8 with BOM, invariant
decimal amounts without symbols, ISO dates); the Spending CSV has every group and category
regardless of the drill level. PNG export (PRD 9.8) is not implemented in this part.

## Consequences

- Spending, income and the budget agree by construction with default toggles; switching toggles
  never changes the budget.
- Each report is at most two `GROUP BY` statements: on the 100k fixture every report query takes
  under about 210 ms (spending and income over five years), well under the 500 ms target.
- Upcoming bills and the forecast low point on Home stay in their designed "available after
  recurring detection" state until the recurring and forecast services exist.
