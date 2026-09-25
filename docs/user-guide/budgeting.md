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
