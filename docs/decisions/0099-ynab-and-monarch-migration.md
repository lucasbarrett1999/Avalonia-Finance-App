# 99. YNAB and Monarch migration importers

- Status: Accepted
- Date: 2026-09-25
- Milestone: M9 (stream D)

## Context

PRD 9.10 step 1 lists "Import from YNAB/Monarch export" (P1) and PRD 14 open question 2 is answered: build
the importers. Their exports differ from bank files: every account is in one CSV, rows carry the user's own
categories (YNAB: group and category; Monarch: category only), YNAB carries cleared state and flags, Monarch
tags; YNAB also exports the budget (assigned per category and month). F-TXN-1 requires every source to go
through the unified pipeline.

## Decision

- **Parsers** (`Import/Ynab/YnabRegisterParser`, `YnabBudgetParser`, `Import/Monarch/MonarchImportParser`,
  sharing `AppExportTable`) are ordinary `IFileImportParser`s recognized by their header only (never by the
  `.csv` extension), so the resolver tries them before the generic CSV layout detector. New
  `ImportFileFormat` values `Ynab`, `YnabBudget`, `Monarch`; `ParseResult.IsMigration`. YNAB amounts are
  Inflow minus Outflow with the decimal separator detected over the column; YNAB's per-budget date format is
  detected over the whole column (ambiguity is reported as for CSV). Account names go to
  `SourceAccountId`; everything else app-specific stays in `Extras` (`Category Group`, `Category`, `Flag`,
  `Cleared`; `Original Statement`, `Tags`). The budget export goes to the new `ParseResult.BudgetRows`.
  Monarch's payee is the merchant (the original statement when empty). No existing parser changed.
- **Pipeline additions** (`IncomingTransaction`): `Status` (the source's cleared state), `IsApproved` (the source
  already reviewed the row) and `Tags` (names; the import creates missing tags by name, ignoring case, and tags
  inserted rows after the bulk insert in the same unit of work). All default to the old behaviour.
  `ImportService.ImportCoreAsync` became `internal static` with the hooks as a parameter so several batches can
  run in one unit of work; `LedgerWriter.DryRunAsync` runs a unit of work and rolls it back.
- **Migration service** (`IMigrationImportService`): `PlanAsync` lists the file's accounts (rows, dates, net,
  an open account with the same name, a suggested type from the name: card/credit/visa/… credit card,
  line of credit/HELOC, savings, cash/wallet, else checking) and the categories and tags to create.
  `ImportAsync` is one `LedgerWriter` action (`ImportTransactions`, one undo entry): new on-budget accounts
  (as the account service creates them, including the Credit Card Payment category; no starting-balance row,
  because the export's own rows make up the balance), missing groups and categories (matched by name ignoring
  case, hidden ones included; added at the end), then each chosen source account's rows through
  `ImportCoreAsync` with source `File`: dedup, payees, rules and learner hooks, transfer detection across the
  accounts already imported in the same run, bulk insert. `PreviewAsync` is the same work rolled back, so the
  preview equals the import. Re-importing the same file adds nothing (fingerprints).
- **Mapping rules**: YNAB `Inflow: Ready to Assign` (or `To be Budgeted`) and Monarch income categories
  (Paychecks, Interest, Business Income, Other Income) become Inflow: Ready to Assign. YNAB rows without a
  category or in "Credit Card Payments", YNAB `Transfer : X` payees and Monarch Transfer / Credit Card Payment /
  Balance Adjustments get no category and count as transfers (pairing is left to transfer detection).
  Monarch categories go to Monarch's default group (`MonarchCategories`), unknown ones to "Other". Rows with a
  category, and transfer rows, are imported approved; the rest go to Review. YNAB Cleared/Uncleared/Reconciled
  keep their status. A YNAB flag of any colour becomes the reserved "Flagged" tag; Monarch tags are split on commas.
  YNAB split sub-rows stay separate transactions (the pipeline has no split input).
- **Budget export** (`ImportBudgetAsync`, action `AssignBudget`, with a rolled-back preview): the assigned
  amount per category and month, creating missing categories; Inflow rows are skipped (Ready to Assign is
  computed, PRD 6.4); Credit Card Payments rows go to the card's payment category with the same name, or are
  skipped. An absent row means 0, so a zero amount removes an existing assignment; equal amounts are unchanged.

## Consequences

- Import summaries count uncategorized rows per batch before later batches pair transfers, so the first
  account's transfer rows can be counted as uncategorized in the toast; the ledger itself is right.
- Payee names come from the export's payee text through the normal naming (title case of the normalized
  descriptor unless a payee of that name exists).
- Only on-budget types can be created from the preview; tracking accounts must exist first and are chosen
  from the list.
