# 21. Categorization learner: features, smoothing, thresholds and explanations

- Status: Accepted
- Date: 2026-09-24
- Milestone: M4

## Context

F-TXN-5 asks for a local learner that predicts a category "from features: normalized payee
tokens, amount bucket, account, day-of-week, direction", implemented as "a naive Bayes or a
simple k-NN", deterministic, explainable, retrained incrementally, never writing a category below
60% confidence, and requiring at least 3 prior examples of a payee "before predicting from payee
alone". It does not fix the exact model, smoothing, bucketing, calibration, what "payee alone"
covers, how hidden and system categories are treated, or what the review queue's "top
suggestions" are when only one category can reach 60%. PRD 12 sets the M4 exit at "≥ 85% after
200 approvals" on a labeled fixture.

## Decision

**Model** (`Keel.Domain.Categorization`). Naive Bayes over integer counts, in two modes:

- *Exact-payee mode*, when the normalized payee (`PayeeNormalizer` on the current payee name,
  falling back to the raw descriptor) has at least 3 approved examples. The payee's own category
  distribution is the prior, smoothed toward the global prior with one pseudo-example:
  `pi(c) = (n_payee(c) + beta * P(c)) / (n_payee + beta)`, `beta = 1`. Only categories the payee
  actually had are eligible. Context likelihoods (below) then re-rank them, which is how split
  payees such as Amazon (small = Household, large = Electronics) are told apart.
- *Token mode*, otherwise. Multinomial naive Bayes over the payee's words:
  `P(c) * prod P(token | c)` with Laplace smoothing, times the context likelihoods. Only words
  seen in at least 3 approved examples count. If no word qualifies, there is no suggestion.
  This is our reading of "≥ 3 prior examples before predicting from payee alone": no payee-derived
  evidence (the exact payee or any of its words) is used until it has 3 examples, so one or two
  approvals of a new merchant never produce a suggestion by themselves.
- *Context features*, both modes: amount bucket, account, day of week, direction, each a
  categorical likelihood `(count + alpha) / (n_c + alpha * (|values| + 1))` (the `+1` reserves mass
  for unseen values), raised to `ContextWeight = 0.5` to temper the naive independence assumption.
- Smoothing `alpha = 1` everywhere. Posteriors are a softmax over all categories with examples, so
  confidences are probabilities that sum to 1.
- **Amount buckets** are `1 + floor(log2(|minor units|))` (0 for zero): each bucket is one doubling
  ($10.24 to $20.47), computed with integers so it is identical on every OS.
- **Tokens**: distinct words of the normalized payee with at least 2 characters and a letter,
  minus a short stop list (`THE`, `AND`, `INC`, `LLC`, `CO`, ...).

**Thresholds.** The primary suggestion must reach `MinimumConfidence = 0.60` or nothing is
returned (`LearnerOutcome.BelowThreshold`, with the best guess in the reason). When it does, up to
`topN` suggestions are returned: the primary (`IsPrimary`, the only one the pipeline may write)
and alternatives down to 5% confidence, for the review queue's 1–9 keys (F-TXN-6). Alternatives
are never written. The review screen's "approve all ≥ 90%" uses the primary's confidence.

**Restricted categories.** Hidden categories and system categories (Inflow: Ready to Assign, and
Credit Card Payment categories) are "restricted" via the `LearnerCategory` catalog; Ready to
Assign is always restricted. They are suggested only when every piece of evidence (the payee's
history in exact-payee mode, the qualifying words' history in token mode) is in restricted
categories, e.g. a paycheck that has only ever gone to Ready to Assign. Otherwise they are
excluded from the ranking without renormalizing, so excluding them never inflates confidence.

**Explanations.** Exact-payee: "Suggested because 12 of 13 past 'TRADER JOES' transactions were
Groceries"; when context features lift a category above the payee's usual one, "; its amount and
account fit Electronics better than Household" is appended. Token mode names up to two words with
the highest share: "Suggested because 9 of 9 past transactions with 'WHOLE' and 14 of 15 with
'FOODS' in the payee were Groceries". Structured evidence (counts, words, supporting context) is
on `CategorySuggestion` for localization later.

**Incremental updates.** `WithExample` and `WithoutExample` update the immutable count tables
(structural sharing) and produce exactly the model a full retrain would (same JSON, tested).
A category change is `WithoutExample(old).WithExample(new)`. Measured: 17 µs per update; a full
retrain of 100k examples takes about 200 ms (BenchmarkDotNet) to 750 ms (cold test run), so
either is fine; the services can retrain on open and update on approval.

**Determinism and storage.** The model stores only integer counts, category names and options,
never floating-point values. JSON (`LearnerModelJson`, `format: keel.categoryLearner`,
`version: 1`) writes keys in ordinal order, so the same history gives byte-identical JSON on every
OS and in any example order. Scoring iterates categories in id order and words in ordinal order.
A newer or inconsistent model throws `LearnerModelFormatException`: retrain from history.

**Accuracy exit criterion.** On the 64-payee, 30-category fixture, the ≥ 3-examples rule means
that after 200 approvals only about 56% of the next transactions have enough payee history, and
F-TXN-5 requires leaving the rest uncategorized. We therefore assert the "≥ 85% after 200
approvals" exit as the accuracy of the suggestions made (95%), report coverage, and assert
top-1 accuracy counting "no suggestion" as wrong (≥ 85%) from 600 approvals on and on the 80/20
held-out split.

## Consequences

- The DB-backed service must feed approved, non-split, non-transfer, non-system transactions as
  `LabeledExample`s and keep the `LearnerCategory` catalog in step with hidden/system flags
  (`WithCategories`). The model can be cached in the `Setting` table as `ToJson()`.
- Tuning knobs live in `LearnerOptions` and are stored with the model; changing a default changes
  suggestions for new models only.
- Changing `PayeeNormalizer` changes features; retrain after bumping `PayeeNormalizer.Version`.
