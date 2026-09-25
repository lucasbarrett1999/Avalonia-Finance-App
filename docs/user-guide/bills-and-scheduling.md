# Bills and scheduling

## Recurring bills, subscriptions and income

Keel looks through your history for things that repeat (rent, phone, streaming, paychecks) once a day
and after every import. New finds are marked **Detected**: open **Bills** and confirm them. Only
confirmed items appear in the forecast and are checked for missed charges.

The Bills screen has three tabs (`1`, `2`, `3`):

- **Calendar**: the month with what was paid, what is expected and what is scheduled on each day.
- **List**: every item with next date, payee, amount, cadence, category, account and status; sortable.
- **Subscriptions**: subscriptions with monthly and yearly totals and their price history.

Select an item for its detail panel: an amount history chart, why it was detected, and actions (confirm,
edit, pause, resume, dismiss, detect again, create a scheduled transaction, create a budget target).
**Add item** (`N`) adds one by hand; **Run detection now** (`R`) checks again.

**What counts as a subscription**: by default Keel decides from the payee and cadence. In
**Settings → Bills and subscriptions** you can choose category groups and tags whose items are always
subscriptions.

## Alerts

The bell in the top bar collects alerts: a price increase of more than 5%, an expected charge three or
more days late, a newly detected recurring item, and the first real charge after a free trial. Each
alert appears once; dismiss it or open the item.

## Scheduled transactions

Use **Schedule** in a register to create a transaction that repeats: daily, weekly on chosen days,
monthly on a day or the Nth weekday, twice a month, or yearly, every N periods, from a start date,
forever, until a date or N times. The editor describes the rule in words and lists the next five
dates. Upcoming instances show in the register as italic **ghost rows** (enter now, skip, edit).
Due instances are entered automatically, or Keel asks first if you prefer.

## Cash-flow forecast

**Reports → Cash-flow forecast** projects each account's balance for the next 90 days from today's
cleared balances, scheduled transactions, confirmed recurring items and, optionally, your usual
day-to-day spending. It marks the lowest point and the days below a floor you set; every day can show
its math. Home's Forecast card shows the lowest-balance account at a glance.
