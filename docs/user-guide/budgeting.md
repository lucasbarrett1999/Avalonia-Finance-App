# Budgeting in Keel

Keel uses envelope (zero-based) budgeting: you give every dollar you have a job before you spend it.

## The numbers

- **Ready to Assign** (the pill at the top of Budget): money in your on-budget accounts that has no
  job yet. Income and starting balances land here. Green when zero or more; red when you assigned
  more than you have ("You've assigned more than you have").
- **Assigned**: what you gave a category this month.
- **Activity**: what was spent from (or refunded to) the category this month. Click it to see the
  transactions.
- **Available**: last month's Available plus Assigned plus Activity. It is what you can still spend.
  - Green: money left. Gray: zero.
  - Yellow with a card icon: you overspent **on a credit card**. The card now carries debt that no
    category covers; assign money to fix it.
  - Red with a warning icon: you overspent **in cash**. At the end of the month the overspending is
    taken from next month's Ready to Assign.

Every number can explain itself: select a category and open the **inspector** (`I`) to see "How is
Available computed?" (carry-over, assigned, activity per account, card spending and what is covered).
With no category selected it explains Ready to Assign.

## Assigning

- Select a cell with the arrow keys or the mouse; type a number (or press `Enter`) to edit Assigned.
  `Enter` saves and moves down, `Tab`/`Shift+Tab` save and move to the next/previous category, `Esc`
  cancels. Amounts accept arithmetic: `120+45.50`.
- **Quick assign** (`Q`, the inspector, or right-click a row): assigned last month, spent last
  month, the three-month average assigned or spent, fund the target, or reset to zero.
- **Fund targets** (`Ctrl+Shift+F`) assigns what every underfunded target needs this month.
- **Move money** (`M`, or drag an Available pill onto another row or onto Ready to Assign) moves money
  between categories. Overspent categories are preset to be covered.
- Change month with `Alt+←`/`Alt+→` or the month picker. You can assign in future months.

## Three months side by side

Press `W` (or the columns button in the Budget header, or **View → Three-month budget**) to see the
shown month and the next two next to each other. Each month has its own Assigned, Activity and
Available columns, group rows add up per month, and each month's header shows its Ready to Assign
(select it to see how it is computed).

The cell cursor moves across all three months with the arrow keys: `→` from a month's Available goes
to the next month's Assigned. Everything you do happens in the month the cursor is in: typing,
`Enter` and `Tab` edit that month's Assigned (Tab stays in the same month), `M` moves money in that
month, and the inspector explains that month. `Alt+←`/`Alt+→` shift the three months by one; the
month picker starts the three months at the month you pick. Press `W` again for one month. Keel
remembers your choice.

Three months need a wide window. On a small screen (or at 200% scaling) the grid scrolls sideways and
keeps the cursor's month in view; hiding the inspector (`I`) makes room for all three.

## Flex view

The Flex view (`F`, the gauge button, or **View → Flex view of the budget**) shows the same month as
one number instead of the grid. Every category counts as one of:

- **Fixed**: the same cost every month (rent, subscriptions, loan payments).
- **Non-monthly**: money set aside for costs that come less often (insurance, car repairs, gifts).
- **Flex**: everything else you spend flexibly (groceries, eating out, fun).

The Flex view shows the month's **income** (what arrived for Ready to Assign), the **Fixed** total
assigned, the **Non-monthly** total set aside (and how much those categories hold so far), and one
**Flex** number: what the Flex categories have to spend this month (carried over plus assigned). Its
bar shows how much of it you spent, with a line for today; below it, how many days are left and how
much you can spend per day and stay within it. If you spend more than the Flex number, the bar turns
red and shows how much you are over.

Nothing changes underneath: your assignments stay as they are, and every number is a sum of the
numbers in the grid. Select a number to open the grid with just its categories ("Show all
categories" brings back the rest); Income opens this month's income in the register.

**Choosing a category's group.** Categories start as **Automatic**: a category whose target asks for
the same amount every month (monthly set-aside, monthly spending or a debt payment) counts as Fixed, a
savings-by-date target counts as Non-monthly, and a category without a target counts as Flex. To
decide yourself, pick Fixed, Non-monthly or Flex in the inspector (**Flex view group**) or in
**Manage categories**; Keel never changes a group you picked, and `Ctrl+Z` undoes the change. Credit
card payment categories belong to no group: card spending already counts in the category it was
spent from.

## Targets

Select a category and press `T` (or use the inspector). Target types:

- **Monthly set-aside**: assign X every month (for example, saving for car repairs).
- **Monthly spending**: have X available each month; Keel refills up to X (groceries).
- **Savings balance by date**: reach X by a date; Keel works out the monthly need (a holiday).
- **Debt payment**: pay X per month toward a linked loan or card.

The row shows "Needs $X" until the month is funded, then "Funded". Goals (see [Reports and goals](reports-and-goals.md)) are categories with a
balance-by-date target.

## Credit cards

Each credit card has a category in **Credit Card Payments**. When you spend on the card from a
budgeted category, that amount moves from the spending category to the card's payment category, so
the money to pay the bill is set aside automatically. The payment row shows the card balance and what
is not yet covered. Pay the card with a transfer from checking; it does not count as spending.

## Categories

**Manage categories** (the list button in the Budget header) adds, renames, reorders, hides and
deletes groups and categories. Deleting a category with history asks where its money and
transactions should go. Each category and each month can have a note (inspector).

## Tracking accounts and transfers

Transfers between two on-budget accounts are not income or spending. A transfer to a tracking
account (for example to an investment account) is spending from the budget, so it needs a category.
