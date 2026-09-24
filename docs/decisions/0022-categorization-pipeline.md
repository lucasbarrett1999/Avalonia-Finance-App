# 22. Categorization pipeline: order, payee default and trace

- Status: Accepted
- Date: 2026-09-24
- Milestone: M4

## Context

F-TXN-1 step 4 applies categorization rules, then the learner "if no rule matched". F-TXN-9 adds a
default category per payee. F-TXN-6 shows up to five suggestions, "rule match first, then learner
suggestions with confidence and the explanation string". The PRD does not say where the payee
default sits, what "no rule matched" means when a rule only renamed the payee, or what the review
screen shows as the reasoning.

## Decision

`Keel.Application.Categorization.ICategorizationEngine` (implemented by the stateless
`CategorizationEngine`, no persistence) runs:

1. **Rules.** A rule "decides" the category when it sets a category, splits the transaction, or
   makes it a transfer (`RuleMutations.DecidesCategory`). Then the payee default and the learner are
   skipped, and the suggestion list is the rule's category at confidence 1.0 ("Set by rule 'X'").
   A rule that matched but only renamed, tagged or flagged does **not** decide; its changes are kept
   and the next stages see the renamed payee.
2. **Already categorized.** A transaction that arrives with a category, splits or a transfer
   account keeps it (`CategorizationSource.Existing`); nothing is suggested.
3. **Payee default** (F-TXN-9). Suggested and written with confidence 0.95 ("Suggested because
   AMAZON's default category is Electronics"). The learner still runs, only to add alternatives
   after it.
4. **Learner.** Its primary suggestion (≥ 60%) is written; alternatives follow. Below 60%, or
   without enough payee history, the transaction stays uncategorized and the learner's reason is
   the summary.

The result carries the rule application (mutation set and rule trace), what decided
(`CategorizationSource`), the category to write, up to `topN` distinct suggestions (primary first),
and a `CategorizationTrace`: one step per stage (decided, no decision, skipped, with an English
detail), the rule trace, the learner prediction with its top candidates, and a one-line summary.

## Consequences

- The import pipeline writes `CategoryId` when `DecidedBy` is `PayeeDefault` or `Learner`, and the
  `RuleMutations.Changes` fields in all cases; imported rows stay unapproved unless a rule marked
  them approved.
- The review screen shows `Suggestions` (1–9 keys) and `Trace.Summary`, with `Trace.Steps` behind a
  "Why?" affordance (principle 7).
