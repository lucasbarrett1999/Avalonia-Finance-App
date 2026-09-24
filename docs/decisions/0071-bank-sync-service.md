# 71. Bank sync service

- Status: Accepted
- Date: 2026-09-24
- Milestone: M7

## Context

F-TXN-3 and PRD 7.5 define the provider interface and ask the sync service to loop until `HasMore`
is false, map pages to the import pipeline, store the cursor only after a batch commits, and record
health. Several details are open.

## Decision

- **Connection ids.** A provider creates the connection id when a link completes (a v7 Guid as a
  string) and stores the token under `SecretKeys.Connection(id)`; the service creates the
  `SyncConnection` with that id only when the user saves the account mapping. Cancelling the mapping
  removes the item at the provider and deletes the token. Appended to the contracts:
  `LinkResult.ExternalItemId`, `LinkSession.OpensBrowser`, `SyncResult.IsHistoryComplete`.
- **Pages.** For each page, `Added` and `Modified` rows are grouped by provider account and sent as
  one `ImportBatch` per linked, open account (`SyncConnectionId` set, source Provider), so dedup by
  provider id (modified rows) and pending id (pending to posted) is the pipeline's, as for files.
  Rows of skipped provider accounts are ignored. Then removed ids are soft-deleted, then the cursor is
  saved. A crash between commit and cursor save repeats the page; provider ids absorb it. Plaid's
  `TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION` restarts from the cursor the sync began with (up to 3
  times).
- **Removals** are one `DeleteTransactions` action: reconciled rows are kept; a removed transfer side
  is unpaired from its partner instead of deleting the partner (it may be the user's own row).
- **Balances** are read once per sync, before the pages, and ride on the last page's batch of each
  account as `ImportBatch.ReportedBalance` (a Provider `BalanceSnapshot` for the local date and the
  account's reported balance). Balances are in Keel's sign convention: credit and loan balances are
  negative.
- **New accounts start at the bank.** An account created by the mapping dialog gets a cleared,
  approved "Starting Balance" (as in F-ACC-1) once the provider says the history is complete:
  reported balance minus the cleared sum, dated on the earliest synced row, which also becomes the
  opening date. A `Setting` marker (`sync.startingBalance.<id>`) makes this happen once. Linking an
  existing account adds nothing; the register's mismatch hint suggests reconciling.
- **Connection state is not ledger data.** Cursor, health, last sync and last error are saved
  directly (not audited or undone), like import memory (ADR 0051). Account links and the imported
  rows go through `LedgerWriter`, so they are audited and undoable. Undoing a sync's import removes its
  rows; they come back only if the provider sends them again (the cursor has moved on).
- **Health.** A provider failure is a `BankProviderException` with a status and code; the service
  records it on the connection (`NeedsReauth`, `Error`) and returns it as a result, never throws it
  to the UI. Error codes, never messages with data, are stored in `LastError` and logged.
- **One sync at a time**; sync-all runs connections one after another. Preferences
  (`SyncSettings`: interval, sync on start) are in the `Setting` table (`sync.settings`) because
  connections belong to the budget file.
- **Unlink** removes the item at the provider (best effort: a provider error is logged and the local
  unlink still happens), unlinks the accounts in one undoable account edit, keeps every transaction,
  and deletes the connection row.

## Consequences

- Sync, file import and manual entry produce the same rows (PRD 1.1 principle 2).
- A missing token on another machine shows as "sign-in needed" instead of an error dialog.
