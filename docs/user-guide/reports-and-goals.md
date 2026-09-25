# Reports and goals

## Reports

**Reports** has five reports (`1`–`5`), each with a toolbar: date range (presets or custom), accounts,
**Include transfers** and **Include tracking accounts**, **Export CSV** (`Ctrl+E`) and **Export PNG**
(`Ctrl+Shift+E`), which saves the report's chart as an image at twice its on-screen size.

- **Spending**: a donut by category group with the total and the change from the previous period.
  Click a slice to see its categories, and again to see the transactions.
- **Income vs expense**: monthly bars with a net line and a table.
- **Net worth**: assets minus liabilities at each month end, optionally stacked by account. Tracking
  accounts use the latest balance you recorded (Record balance in their register).
- **Cash-flow forecast**: see [Bills and scheduling](bills-and-scheduling.md#cash-flow-forecast).
- **Budget health**: four numbers for the last month of the range (at most this month), each with the
  figures behind it, and the age of money at each month end of the range:
  - **Age of money**: how many days, on average, your money sits between arriving and being spent.
    Money that comes into your checking, savings and cash accounts is spent oldest first; the number is
    the average over your last 10 outflows. Moving money between those accounts doesn't count, and card
    purchases count when you pay the card. A growing number means you are spending older money: you
    are getting ahead.
  - **Months ahead**: Ready to Assign plus every positive Available (card payment categories left out),
    divided by your average monthly spending over the last three complete months.
  - **Targets funded**: how much of what your targets ask for this month is assigned, and how many
    targets are fully funded.
  - **Overspent categories**: how many categories are below zero this month; cash overspending comes out
    of next month's Ready to Assign. Click one to see its transactions.

  The accounts filter and the transfer toggles don't apply: budget health covers the whole budget.

Every slice, bar, point and table row opens the underlying transactions. Colors are never the only
signal: every chart has a legend or table with names and amounts.

With the default toggles, Spending equals the negative of the budget's Activity for the same months.

## Goals

A goal is a budget category with a target amount and date. **Goals → New goal** (`N`) asks for a
name, the amount, the date, and optionally a savings account it lives in, then creates the category (in
a "Goals" group) with a savings-balance-by-date target. Each goal card shows a progress ring, the
monthly amount needed, the projected completion at your recent pace, and a "what if I added $X a
month" slider. Fund goals from Budget like any other category.

### Debt payoff

The **Debt payoff** tab (`2` on Goals, `1` goes back) plans paying off your credit cards, lines of
credit, loans and mortgages. Add each one's **interest rate (APR %)** and **minimum payment** in
**Edit account**; debts without them are listed at the bottom with an Edit account button.

- The table shows each debt's balance, rate, minimum, this month's payment, when it is paid off and the
  interest until then under your plan, and below it the same at the minimum payment alone.
- **Extra per month** is added on top of the minimums. **Pay first** chooses the order: **Avalanche**
  (highest rate first, least interest) or **Snowball** (smallest balance first, quickest wins). The extra
  goes to the first unpaid debt; when a debt is paid off, its minimum rolls over to the next one.
- The headline says when you are debt-free and how much sooner and cheaper that is than paying only the
  minimums. A debt whose payment doesn't cover its interest is marked **Never**; raise the extra.
- The chart shows each debt's balance month by month, with the minimum-payments-only total as a dashed
  line. Click a row or a band to open the account.
- **Set payment targets** sets a debt-payment target equal to this month's planned payment on each
  debt's payment category (a card's payment category, the category you use for payments to a loan, or a
  new "{loan} payment" category in a "Debt payments" group). Undo (`Ctrl+Z`) removes them all at once.
  Interest is estimated as the yearly rate divided by twelve on the balance at the start of each month.

## Home

Home shows Ready to Assign (Assign opens Budget), the Review queue, your accounts, bills due in the
next seven days, overspent or underfunded categories, the forecast low point, net worth over the last
twelve months, and your age of money (Budget health opens the report).
