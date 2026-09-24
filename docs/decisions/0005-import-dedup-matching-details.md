# 5. Import deduplication: passes, one-to-one matching and normalizer additions

- Status: Accepted
- Date: 2026-09-24
- Milestone: M3

## Context

PRD 6.5 lists five dedup steps "in order" and says an exact fingerprint match is skipped "if it
exists". Taken literally, per row and per existing transaction, three things go wrong:

1. **Identical rows.** Two genuine, identical transactions (two $5 coffees at one shop on one
   day) have the same fingerprint. If a later, overlapping statement lists both while an
   earlier import stored only one, "exists" skips both and one purchase is lost.
2. **Order dependence.** If each row runs all five steps before the next row starts, an early
   row can fuzzy-match (step 4) a manual transaction that a later row matches exactly (step 3).
   The later row is then inserted as a duplicate.
3. **Repeated fuzzy matches.** A manual row that already absorbed one imported row can absorb
   another, different imported row a day later, hiding a real transaction.

PRD 6.5 also names the normalizer's noise list only by example ("etc.").

## Decision

`Keel.Domain.Import.DuplicateMatcher`:

- Runs the steps as **passes over the whole batch** in PRD order (provider id, pending to
  posted, fingerprint, fuzzy, insert). A row settled by an earlier pass is not considered by a
  later one.
- **Matches each existing transaction at most once per batch** in the pending, fingerprint and
  fuzzy passes. The provider-id pass is a key lookup and may match an already matched row.
  Two incoming rows with the same fingerprint therefore need two existing rows to both be
  skipped. Re-importing an identical batch still yields zero inserts (property-tested).
- **Pending to posted** matches the incoming `PendingTransactionId` against an existing row's
  `ProviderPendingId`, and also against its `ProviderTransactionId`, since a row inserted while
  pending carries the pending id as its provider id.
- **Fuzzy matching** considers only Manual or Scheduled rows that carry no provider id and are
  not flagged `HasImportMatch`, so one manual row absorbs at most one imported row ever. Ties go
  to the highest Jaro-Winkler score, then the closest date, then the earliest (date, id).

`PayeeNormalizer` implements the PRD steps and adds, in `PayeeNoiseTable`: merchant rewrites
(Amazon, Apple, Walmart, Costco, Squarespace, Microsoft), processor prefixes ending in `*`,
bank channel phrases (`PURCHASE AUTHORIZED ON`, `CHECK CARD`, ...), and removal of masked card
numbers, `CARD 1234`, short dates, ACH ids, reference and confirmation numbers, `#123` store
numbers and web decoration. Apostrophes and periods are deleted (`JOE'S` → `JOES`), other
punctuation splits words. If nothing is left, the collapsed upper-case descriptor is returned
so an all-noise descriptor never becomes an empty payee. `PayeeNormalizer.Version` (1) exists
because stored fingerprints depend on the table.

## Consequences

- The import pipeline (next M3 task) must persist `HasImportMatch` (or an equivalent, such as
  "has an attached fingerprint from a file or provider") when it applies a
  `MatchExistingManual` decision, and must store the incoming fingerprint on the matched row.
- Changing `PayeeNoiseTable` changes future fingerprints. Stored fingerprints keep working for
  rows whose `ImportFingerprint` is loaded into `DedupCandidate`, but a re-import of an old
  file after a table change can miss step 3 and fall to step 4 or insert. Bump
  `PayeeNormalizer.Version` and consider recomputing stored fingerprints in a migration.
