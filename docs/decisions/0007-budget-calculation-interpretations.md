# 5. Interpretations of the budget calculation (PRD 6.4)

- Status: Accepted
- Date: 2026-09-24
- Milestone: M2

## Context

PRD 6.4 is normative and `BudgetCalculator` (`src/Keel.Domain/Budgeting/`) implements its
formulas literally; the golden tests for 6.4.7 and 6.4.8 assert the PRD's numbers unchanged.
Some inputs the ledger can contain are not covered by a formula, and a few formulas leave a
detail open. This record states the interpretation chosen for each, so the behaviour can be
reviewed and overridden. Every item has a test in `tests/Keel.Domain.Tests/Budgeting/`.

## Decision

1. **Rounding of `Covered` (6.4.5).** Each card's uncovered share
   `Uncovered × CardSpend(c, K) / CreditSpendingMagnitude(c)` is computed exactly and rounded
   down; the rounding remainder (at most one minor unit per card) is added to the uncovered share
   of the card with the largest `CardSpend` (ties: the account that comes first in account
   order), so that card's `Covered` is the one reduced. If that card cannot absorb the remainder
   without `Covered` going below 0 (only possible with sub-cent spends), the rest spills to the
   next largest card. Guarantees: `Σ_K Covered = Magnitude − Uncovered` and
   `0 ≤ Covered(c, K) ≤ CardSpend(c, K)`.
2. **Payment categories overspent.** No card activity is ever categorized to `Pay_K`, so
   `CreditSpendingMagnitude(Pay_K) = 0` and a negative `Available(Pay_K)` (a payment larger than
   the money set aside, e.g. paying down debt that was never budgeted) is **cash** overspending:
   red, and it reduces the next month's Ready to Assign. This is the literal 6.4.4 result.
3. **What counts as a payment.** `Payments(K, M)` sums the card-side rows of transfers that are
   uncategorized, positive (money into K), and whose other account is an on-budget cash account.
   A categorized transfer into a card (e.g. from a tracking account) is ordinary categorized
   activity on the card (a refund for that category). An uncategorized transfer out of a card
   (a cash advance recorded as a plain transfer) contributes nothing: the debt stays uncovered.
4. **Cash advances.** As 6.4.5 says, both sides carry a category: the cash side is income
   (default Ready to Assign) and the card side is a spending category whose card spend is
   covered by the usual rule. Nothing about transfers is special-cased; the category decides.
5. **Ready to Assign on credit accounts.** `InflowRTA` sums rows categorized Ready to Assign in
   all on-budget accounts, credit included (e.g. cash-back rewards posted to a card).
6. **Uncategorized activity.** Rows in on-budget accounts with no category that are not
   transfers, and rows categorized directly to a Credit Card Payment category (which 6.4.5 does
   not define), are outside every formula. They affect no category and not Ready to Assign; the
   month result reports their sum as `UncategorizedActivity` so the UI can prompt the user to
   categorize them. Transfers between on-budget accounts have no category and are ignored.
7. **Month totals vs group rows (6.4.3).** Group rows sum the visible categories only. The month
   header totals (Σ Assigned, Σ Activity, Σ Available) include hidden categories, so that
   Σ Assigned equals `TotalAssigned(M)` of 6.4.1, which counts every non-Inflow category.
8. **Hidden categories.** Hiding is a current flag, not historical. A hidden category (or a
   category in a hidden group) is computed for every month like any other: its assignments
   reduce Ready to Assign, its balance carries, its overspending counts. It is only left out of
   group rows.
9. **"Assigned in future months" (6.4.1)** is unbounded: every assignment in any later month
   counts, including months after the requested range.
10. **Where the recursion starts (6.4.2).** Computation starts at the earliest month with any
    activity, assignment or payment, or at the requested start if earlier. Because the carry
    is floored at 0 and absent assignments are 0, this is equivalent to starting each category at
    its own first month.
11. **Closed and tracking accounts.** Closed accounts are included in every month (history must
    not change when an account is closed). Tracking accounts are excluded entirely, including any
    row in them that carries a category; only the on-budget side of a transfer to or from a
    tracking account counts.
12. **Assignments to Inflow categories** are ignored (6.4.1 excludes Inflow from
    `TotalAssigned`); the budget service rejects them. Payment categories can be assigned.
13. **Months** are civil calendar months of the `DateOnly` ledger date (`BudgetMonth.Of`); any
    date in a month addresses that month.

## Consequences

- The calculator's results are fully determined by the ledger and assignments (principle 1)
  and independent of input order and of the requested range (tested against a naive reference
  transcription of the formulas on generated inputs).
- If the owner prefers another rounding direction or wants uncategorized rows to reduce Ready to
  Assign, the change is local to `BudgetCalculator` and the affected golden files.
