# 96. Tags in the ledger: editing, search, management and reserved tags

- Status: Accepted
- Date: 2026-09-25
- Milestone: M9 (stream C, F-TXN-8)

## Context

F-TXN-8 asks for free-form tags. The `Tag` and `TransactionTag` tables, `ITagService.GetTagsAsync` and the
`tag:` search key existed since M1/M8, and two features already store tags: the rules' "flag" action is the
reserved tag `Flagged` (ADR 0026), and Settings → Bills marks subscription tags by id in the `Setting`
table (`recurring.subscriptionTagIds`). The register had no way to see or edit tags, and nothing managed
them. Rules name tags by text in their JSON (`TagCondition`, `AddTagAction`).

## Decision

- **Names.** `TagNames.Clean` trims, collapses whitespace, drops one leading `#` and cuts to the 100-character
  column. Tags compare case-insensitively everywhere (in code, because SQLite's `UPPER` is ASCII-only and
  the unique index on `Tag.Name` is case-sensitive); an existing tag keeps its first spelling. All tag writes
  (editor, rules, imported tags such as a YNAB flag or Monarch's Tags column) go through
  `TagService.GetOrAddAsync`/`SetTransactionTagsAsync`.
- **Editing.** `SaveTransactionRequest.Tags` is the transaction's tag list after the save (null keeps the
  tags). New names become tags inside the same `LedgerWriter` action as the transaction, so one undo removes
  the tag it created. Tags belong to one side of a transfer. The editor's tag box adds on Enter (the
  highlighted suggestion, else the typed text) and removes the last chip on Backspace in an empty box;
  Enter in an empty box saves as before. Text typed but not added is saved too. `T` on a register row opens
  the editor with the tag box focused.
- **Register.** Rows carry their tag names and attachment count (read for the page only, two bounded
  queries like the splits); chips sit before the memo and are clipped, never overlapping. The filter bar
  gains a tag filter (`RegisterFilter.TagId`, shown when the file has tags); free words also match tag
  names; `has:tag` and `has:attachment` join the F-TXN-7 syntax. When the register is narrower than 1000 px the filters
  move under the search box.
- **Management** (Settings → Tags, one undoable action each; tag, link, rule and setting rows are all tracked
  so undo restores them, never the database cascade):
  - *Rename* refuses an empty name, a name another tag has ("merge instead") and anything involving
    `Flagged`. Rules whose conditions or actions name the tag are rewritten to the new name.
  - *Merge* moves every link (deleted transactions' too) from the source to the target, dropping
    duplicates, rewrites rules, moves a subscription designation to the target (the union), and deletes
    the source. `Flagged` cannot be merged either way (merging into it would flag transactions; merging
    it away would unflag them silently).
  - *Delete* removes the links and the tag and drops it from the subscription designations, after a
    confirmation with the transaction count. Rules are not rewritten: the confirmation says how many
    rules still add the tag (they create it again when they run). Deleting `Flagged` unflags everything.
- **Change messages.** Tag links and attachments publish `LedgerChanged` for their transaction's account
  and month (`LedgerWriter` looks the transaction up, as for split lines); a rename of a tag without links
  publishes an "any account" change.

## Consequences

- Recurring items classified as subscriptions through a deleted or merged tag are reclassified by the next
  detection run, not immediately.
- `LedgerText` looks up texts of later features under their feature prefix (`Tag_Error_…`,
  `Attachment_Action_…`), so new ledger errors and actions do not need keys outside the feature's prefix.
