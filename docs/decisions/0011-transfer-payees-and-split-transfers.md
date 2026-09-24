# 7. Transfer payees are synthesized; split lines cannot be transfers in v1

- Status: Accepted
- Date: 2026-09-24
- Milestone: M1

## Context

PRD 6.2 gives `Payee` an `IsTransferPayeeForAccountId` column (YNAB-style "Transfer : Account"
payee rows) and `TransactionSplit` a `TransferAccountId`. F-ACC-4 defines transfers as one logical
record in both registers, and F-ACC-5 defines split lines as category, memo and amount.

## Decision

- A transfer is two `Transaction` rows sharing `TransferPairId`, each pointing at the other
  account through `TransferAccountId`, with `PayeeId` null. No payee rows are created for
  accounts; the UI shows and accepts "Transfer: <account>" in the payee box and the register
  shows the paired account with a transfer icon. Renaming an account therefore needs no payee
  update. `IsTransferPayeeForAccountId` stays in the schema, unused.
- Split lines carry a category, memo and amount only. A transfer cannot be split
  (`LedgerError.SplitTransfer`); `TransactionSplit.TransferAccountId` stays in the schema, unused.

## Consequences

- Fewer rows to keep consistent (and to undo); the payee list only contains real payees.
- Splitting part of a purchase into a transfer (for example "cash back" into the wallet) is not
  possible in v1; it can be added later by creating a counterpart row per transfer split without
  a schema change.
