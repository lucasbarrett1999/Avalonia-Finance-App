# Review and rules

## The Review queue

New transactions from imports and bank sync arrive **unapproved** and wait in **Review** (the badge
in the sidebar is the count). Review shows one transaction at a time, oldest first, with its details,
the bank's original description, and up to five suggested categories:

- a **rule** that matches (always first),
- the payee's **default category**,
- the **learner**'s suggestions, each with a confidence and a short explanation ("You chose Groceries
  for 14 of 15 transactions from this payee"). **Why?** shows the full reasoning.

Every decision approves the transaction and moves to the next one:

| Key | Action |
|---|---|
| `A` | Approve with the current category |
| `1`–`9` | Approve with that suggestion |
| `C` | Choose another category (type to search) |
| `S` | Split across categories |
| `T` | Mark as a transfer to another account |
| `R` | Create a rule from this transaction |
| `D` | Delete (with undo) |
| `J` / `K` | Next / previous without deciding |

**Approve N with confidence ≥ 90%** approves every transaction whose top suggestion is that sure.
In a register, unapproved rows have an accent bar and `A` approves the selected row.

The learner runs on your computer only. It learns from what you approve and correct, needs a few
examples of a payee before it suggests anything, and never assigns a category below 60% confidence.

## Rules

Rules change incoming transactions before you see them. Manage them in **Settings → Rules** or
**Review → Manage rules**; create one from any transaction (Review `R`, or right-click a register row
→ **Create rule from this transaction…**).

- **Conditions**: payee or memo (contains, equals, starts with, matches a pattern), amount (equals,
  above, below, between), inflow or outflow, account, source (file, bank sync, manual), date range,
  tag. A rule needs all conditions, or any one of them.
- **Actions**: rename the payee, set the category, set or append to the memo, add a tag, flag, mark
  as approved, split by amounts or percentages, mark as a transfer to an account.
- Rules run in order, top first; drag to reorder, switch them on and off. The editor checks the rule as
  you type and **Test rule** shows which existing transactions match.
- **Apply to existing transactions** previews every change before it happens (all transactions or
  only those waiting in Review) and is one undoable step.

## Payees

**Settings → Payees**: search your payees, set a default category (used when no rule matches), and
rename a payee (renaming to an existing name merges the two).
