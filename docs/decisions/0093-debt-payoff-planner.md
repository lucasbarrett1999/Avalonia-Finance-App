# 93. Debt payoff planner

- Status: Accepted
- Date: 2026-09-25
- Milestone: M9 (stream B)

## Context

F-GOAL-2 asks for payoff dates and total interest "at current payment" for loan and credit-card
accounts with a balance, interest rate and minimum payment; "extra per month"; snowball and avalanche
ordering across several debts; and one click that sets debt-payment targets. PRD 9.7 puts it in a
"Debt payoff" tab on Goals. The data model had no rate or minimum payment, and the PRD does not say how
interest is charged, what "rollover" means, when a plan "never" finishes, or which category a debt's
payments are budgeted in.

## Decision

**Data.** `Account` gains `InterestRateBps` (`int?`, annual rate in basis points, 1999 = 19.99% APR)
and `MinimumPayment` (`long?`, minor units), added by the `DebtPayoffFields` migration (two nullable
columns; no table is rebuilt, so the split-sum triggers are untouched). They are edited in the account
editor for liability types (credit card, line of credit, loan/mortgage, other liability); asset types
ignore them. `UpdateAccountRequest.Debt` null leaves them unchanged and `DebtTerms.None` clears them;
rates outside 0–100% and negative minimums are refused. A minimum of zero in the editor means "not
entered". Edits are ordinary audited, undoable account updates.

**Which debts.** Open liability accounts that are owed something. The balance follows the net-worth
rule (ADR 0060): the ledger balance, or for a tracking account with balance snapshots the latest
snapshot plus later activity. Debts without both a rate and a minimum are listed under "Add details to
plan these debts" with an Edit account button and left out of the plans.

**Math** (`Keel.Domain/Debt/DebtPayoffCalculator`, pure and integer-only).

1. Month 1 is the current month. Each month interest is charged first on the balance owed at the start
   of the month: `balance × rate / 12`, rounded half to even to the minor unit (Int128 intermediate).
2. The monthly outlay is Σ minimums of the debts owed at the start plus the extra. Every unpaid debt
   receives its minimum (capped at what it owes); what is left (the extra, plus minimums freed by debts
   already paid off, plus anything left over in a payoff month) goes to the debts in priority order
   until it is used up. So the outlay stays the same every month until the last debt is gone
   ("rollover").
3. Priority is fixed at the start: **snowball** = smallest balance first (ties: higher rate, then
   name, then id); **avalanche** = highest rate first (ties: smaller balance, then name, then id).
4. **Never paid off**: a debt whose monthly interest reaches the whole outlay can never shrink (no month
   pays it more than the outlay and its interest only grows); from that month it is reported as never
   paid off. The plan continues until every other debt is paid off, and runs at most 1,200 months.
   Balances saturate at `long.MaxValue / 1024` so a century of interest cannot overflow.
5. "At current payment" is each debt on its own at its minimum with no rollover (`MinimumOnly`); the
   table shows it under each plan row ("At the minimum alone: paid off … with … interest") and the chart
   draws its total as a dashed line. The plan's comparison line reports months and interest saved.
6. Payoff month *m* is the calendar month `start + (m − 1)`; the chart's point *i* is the balance
   owed at the start of month `start + i`. The chart draws at most 30 years.

**Targets.** "Set payment targets" writes a **debt-payment target equal to this month's planned
payment** (the plan's month-1 payment for that debt) on each planned debt's payment category, linked
to the account, through a new `IBudgetService.SetTargetsAsync` that saves every target as **one
undoable budget action** (`LedgerAction.SetTarget`, the existing budget undo path). The payment
category is resolved in this order: the on-budget credit account's Credit Card Payment category; a
category that already has a debt-payment target for the account; the category used most often on
categorized transfers into the account (the on-budget side of a payment to a tracking loan, PRD 6.3);
otherwise "{account} payment" in the "Debt payments" group, reused if it exists or created (a separate
undo step, like any new category). Targets reflect this month's plan: after a debt is paid off, the
planner shows the new amounts and the button sets them again.

## Consequences

- The planner never writes anything except through the button; changing the extra or the ordering is
  instant (the service reloads accounts and re-plans; plans of a few debts over 30 years take well under
  a millisecond).
- Budgeted card payments already include new spending covered by the budget (6.4.5); a debt-payment
  target on a card's payment category is money assigned on top of that toward the balance.
- Rates are simple monthly APR/12; promotional rates, compounding daily, fees and minimums that shrink
  with the balance are not modelled.
