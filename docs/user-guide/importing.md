# Importing files

Most banks let you download transactions. Keel reads **CSV**, **OFX**, **QFX** and **QIF** files, and
imported transactions are treated exactly like typed or synced ones: the same duplicate check, rules,
suggestions and Review queue.

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
