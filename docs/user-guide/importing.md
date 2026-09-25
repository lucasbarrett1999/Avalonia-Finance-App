# Importing files

Most banks let you download transactions. Keel reads **CSV**, **OFX**, **QFX** and **QIF** files, and
imported transactions are treated exactly like typed or synced ones: the same duplicate check, rules,
suggestions and Review queue. Coming from YNAB or Monarch? See
[Moving to Keel from YNAB or Monarch](#moving-to-keel-from-ynab-or-monarch) below.

## Import a file

1. Open the account's register and choose **Import file** (or right-click the account in the sidebar
   → **Import file…**; from All accounts, or the palette's "Import a bank file…", Keel asks which
   account first).
2. Pick the file. Keel remembers the last folder per account.
3. **CSV only: map the columns.** Keel detects the layout (date, payee, memo, and either one signed
   amount, separate debit and credit columns, or an amount plus a type column) and shows the first
   ten rows as it will read them. Check the date format; when a file could be month-first or
   day-first, Keel asks. Keel remembers the mapping for this account and reuses it when the header
   is the same next time.
4. **Preview.** Every row is listed with its status:
   - **New**: will be added.
   - **Duplicate**: already in Keel (from an earlier import or sync); skipped.
   - **Matched to existing**: a transaction you typed by hand; the imported details are merged into
     it instead of adding a second copy.
   - **Updated**: the bank changed a transaction it sent before (OFX/QFX); updated in place.
   - **Transfer pair**: matches the other side of a transfer in another account; linked as one
     transfer.
   Untick rows you do not want. OFX/QFX files with several accounts ask which statement to import.
   The reported balance, when the file has one, is recorded for the account.
5. **Import.** The status strip shows what happened, with **Undo**. Imported rows are cleared and wait
   in [Review](review-and-rules.md) for approval.

## Importing the same file twice

Is safe. Every imported row keeps a fingerprint (bank id, or date, amount and cleaned-up payee), and
a second import of the same rows adds nothing. Rows you edited or reconciled are never overwritten.

## Tips

- Download overlapping date ranges; duplicates are dropped, so you never miss a day.
- Rows in another currency than the account are skipped, with a warning in the preview.
- Payee names are cleaned (card numbers, store numbers and processor prefixes removed) before they are
  matched to your payees; rename payees in Settings → Payees.

## Moving to Keel from YNAB or Monarch

Keel reads the exports of two budgeting apps and brings in every account, category and transaction in
one step. Keel recognizes these files by their columns, whatever they are called.

**Export from YNAB.** In YNAB choose the budget's menu → **Export budget**. You get a zip with two CSV
files: the **Register** (all transactions: Account, Flag, Date, Payee, Category Group/Category, Category
Group, Category, Memo, Outflow, Inflow, Cleared) and the **Budget** or **Plan** (Month, Category
Group/Category, Category Group, Category, Budgeted or Assigned, Activity, Available). Unzip it.

**Export from Monarch.** In Monarch go to **Transactions** → **Export** (or Settings → Data → Export).
The CSV has Date, Merchant, Category, Account, Original Statement, Notes, Amount and Tags.

**Import.** Use any of these; they all open the same preview:

- first run: **Import from YNAB or Monarch export…** on the Welcome step creates the budget file named
  above it and imports into it;
- **Settings → General → Import from YNAB or Monarch…**, or the command palette;
- **Import file** in a register: when the chosen file is a YNAB or Monarch export, the migration preview
  opens instead of the single-account import.

The preview lists each account in the export with its number of transactions and dates, and where it
goes: a **new account** with the same name (choose its type: checking, savings, cash, credit card or line
of credit; Keel suggests one from the name), one of your **existing accounts** (an account with the same
name is chosen for you), or **Don't import**. Below are the categories, groups and tags Keel will create,
and a line that says exactly what the import will do (it is the import itself, run and rolled back): new
transactions, those already in Keel, transfers matched and accounts to create. It updates as you change
the choices. **Import** runs it all as one action, with **Undo** in the status strip.

What carries over:

| From | In Keel |
|---|---|
| YNAB category group and category, Monarch category | The same category; missing groups and categories are created. Monarch categories go to Monarch's usual group (Food & Dining, Bills & Utilities, …), others to "Other". |
| YNAB "Inflow: Ready to Assign", Monarch income (Paychecks, Interest, Business Income, Other Income) | Inflow: Ready to Assign |
| YNAB "Transfer : …" rows, Monarch Transfer and Credit Card Payment | Matched as transfers between the imported accounts, no category |
| YNAB Cleared column | Uncleared, cleared or reconciled |
| YNAB flag (any colour) | The **Flagged** tag |
| Monarch tags | Tags with the same names |
| Payee, memo or notes | Payee and memo, cleaned up as for any import |

Rows with a category and transfers arrive approved; rows without a category (Monarch's "Uncategorized")
wait in [Review](review-and-rules.md). New accounts have no separate starting balance: YNAB's own
"Starting Balance" rows (and the rest of the history) make up the balance, so compare it with your bank
and reconcile. YNAB split transactions arrive as one transaction per split line. Importing the same export
again adds nothing, so you can re-export later to pick up newer transactions.

**YNAB budget.** Import the Budget/Plan CSV the same way. Keel shows how many months and assigned amounts
it will write, then sets each category's **Assigned** for each month to YNAB's amount (creating missing
categories). Ready to Assign is always calculated by Keel, so YNAB's Inflow rows are left out; Credit Card
Payments rows go to the payment category of the card with the same name. Import the register first so the
cards exist.
