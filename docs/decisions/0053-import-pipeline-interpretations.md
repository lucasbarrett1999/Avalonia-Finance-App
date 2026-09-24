# 53. Import pipeline interpretations

- Status: Accepted
- Date: 2026-09-24
- Milestone: M3

## Context

F-TXN-1 lists the seven pipeline steps and F-TXN-2 the file import and preview; several details
are open.

## Decision

- **One entry point.** `IImportService.ImportTransactionsAsync(source, batch)` serves File now and
  Provider (bank sync) later: provider-id lookups are scoped to the batch's `SyncConnectionId`
  accounts when set. Dedup reads only the target account's rows in the batch's date range ±3 days
  plus provider-id lookups (any date), in chunks of 500 keys.
- **Payees.** An imported row's `PayeeRaw` keeps the source text. Its payee is an existing payee
  whose name normalizes (PRD 6.5) to the same text, else a new payee named after the normalized
  descriptor in title case (`SQ *BLUE BOTTLE 1234` → "Blue Bottle"). The payee's default category
  applies when the source chose none. Transfer sides get no payee (ADR 0011).
- **Rules seam.** `IImportCategorizationHook` (steps 3 and 4) is called for every registered hook
  in order, first to rename payees, then to categorize; a no-op default is registered. M4 adds its
  hook to DI without touching the pipeline. Hooks run in previews too and must not write.
- **Status and approval.** File and provider rows are inserted unapproved and cleared (the bank
  posted them), pending rows uncleared; manual-source rows approved and uncleared. Tracking
  accounts get no category; unknown category ids from a hook are dropped.
- **Dedup edges.** An exact fingerprint match against a manual row is a skip (PRD 6.5 step 3),
  not a match. A provider-id match with no change is reported and counted as a duplicate. An
  update never changes a reconciled row's date or amount (warning `ReconciledNotUpdated`); an
  updated transfer side keeps its partner's date and amount in step. The incoming pending id is
  only a lookup key and is not stored on inserted rows, matching the matcher's reference model
  (ADR 0005); a posted row that replaces a pending row takes over its provider id.
- **Transfers.** The other side must be an existing file or provider row in another open account,
  within ±3 days, not already a transfer, split or reconciled. Pairs `TransferDetector` marks
  ambiguous are shown but not made unless the user checks them. A pair is made like
  `ITransactionService` makes one: shared `TransferPairId`, each side pointing at the other
  account, no payee, category only on the on-budget side of an on/off-budget transfer.
- **Preview choices.** Each row has an "import" checkbox: unchecked rows are not written;
  checking a duplicate inserts it as a new row; unchecking a match or update leaves the existing
  row alone; unchanged provider-id matches cannot be checked. Transfer rows have a second
  checkbox for the pairing. Choices travel as `ImportBatch.Overrides` keyed by row index.
- **Files with several accounts** (OFX statements, QIF blocks): one account is imported into the
  chosen Keel account; the preview offers the others and the summary warns. Rows in another
  currency than the account's are skipped with a warning.
- **Reported balance.** An OFX `LEDGERBAL` (with `DTASOF`, else the statement end) becomes a
  `BalanceSnapshot` with source Provider and, unless an older date, the account's
  `ReportedBalance`, which the register header compares with the cleared balance.
- **Summary.** Added, updated, duplicates skipped, matched to existing, transfers paired,
  uncategorized (inserted rows that need a category and have none), rows skipped by choice, and
  the parse and pipeline warnings. An import that changes nothing is not put on the undo stack.

## Consequences

- The M4 rules engine and learner only register a hook. The review queue (M4) finds imported rows
  by `IsApproved = false`.
