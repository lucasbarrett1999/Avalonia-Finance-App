# 20. Rule format and engine semantics

- Status: Accepted
- Date: 2026-09-24
- Milestone: M4

## Context

F-TXN-4 lists rule conditions and actions and says rules are ordered, "first match wins unless
marked continue". PRD 6.2 stores a rule as `ConditionsJson` and `ActionsJson`, "versioned". Several
points are open: the JSON shape, what a payee condition compares against once a payee has been
renamed, how amounts compare for outflows, what "continue" does when two rules set the same field,
how fixed-amount splits handle the remainder, and what happens to a bad regular expression.

## Decision

**Format** (`Keel.Domain.Rules.RuleJson`). Each column is its own document with a `version`
(1 today): `{"version":1,"match":"all|any","conditions":[...]}` and
`{"version":1,"actions":[...]}`. Every item has a camelCase `type` discriminator
(`payee`, `memo`, `amount`, `direction`, `account`, `source`, `dateRange`, `tag`; `setPayee`,
`setCategory`, `setMemo`, `appendMemo`, `addTag`, `markApproved`, `flag`, `splitByAmounts`,
`splitByPercentages`, `setTransferAccount`). Enums are camelCase strings, nulls are omitted,
unknown properties are ignored (forward-compatible additions), and a missing `version` reads as 1.
A newer version, an unknown `type`, a missing required value or malformed JSON throws
`RuleFormatException`, so an older app never silently rewrites a rule it does not understand.

**Conditions.**
- Text comparisons (payee, memo) ignore case (ordinal) and trim the compared text. Regex uses
  `IgnoreCase | CultureInvariant`.
- A payee condition matches if **either** the current payee name **or** the raw descriptor
  satisfies it, so a rule written against the bank descriptor (`AMZN MKTP`) keeps working after
  the payee is renamed (`Amazon`), and vice versa. With `normalized: true`, both texts and the
  value go through `PayeeNormalizer` (a regex pattern is used as written).
- Amounts compare the **magnitude** by default (`signed: false`), because users think of "more
  than $100" for purchases; direction is its own condition. `signed: true` compares the signed
  amount. `between` is inclusive; `greaterThan`/`lessThan` are strict. Zero is neither inflow nor
  outflow. Date ranges are inclusive and may be open on one side.

**Engine** (`RuleEngine`, `CompiledRuleSet`).
- Rules run in ascending `SortOrder`; ties keep the caller's order (stable sort).
- Disabled rules, and rules with validation errors, are skipped and listed in the trace.
- A matching rule's actions run in order on a working copy; **later rules see earlier changes**
  (a rename with "continue" feeds a category rule keyed on the new name). When two matched rules
  set the same field, the **later one wins** (it ran later on purpose, after "continue").
- The first match without `ContinueAfterMatch` stops evaluation; later rules are not evaluated
  and not listed in the trace.
- The result is a mutation set: the final snapshot, a `RuleChanges` flag set computed by diffing
  input and output, the tags added, and for each changed field the rule that last set it. The
  trace lists each rule's outcome, every condition's result, every action's result (with a note
  when an action did not apply, e.g. "already tagged") and whether it stopped evaluation.
- Actions are idempotent where that matters for retroactive re-application: `appendMemo` skips
  when the memo already ends with the text, `addTag` skips a tag already present (ignoring case).
- `setCategory` replaces existing splits; a split action clears the category. A rule that sets a
  category, splits, or sets a transfer account "decides the category" and the learner is skipped
  (ADR 0022).
- The transaction entity has no flag column yet; `flag` sets `TransactionSnapshot.IsFlagged` and
  the persistence task decides where it lives.

**Splits.** Percentages must sum to exactly 100 and use `Money.SplitByPercentages` (banker's
rounding, remainder on the last line, PRD 6.1). Fixed-amount lines are positive magnitudes that
take the parent's sign; the **last line has no amount and receives the remainder**. If the fixed
lines leave zero or less for the last line, or the parent is zero, the action is skipped with a
trace note instead of producing a sign-flipped or zero split. Splits always sum to the parent.

**Regular expressions.** Patterns are compiled once per `CompiledRuleSet`, with a match timeout
(default 100 ms) and a 500-character limit. An invalid pattern is a validator error (the message
names the parser error and position) and the engine skips the rule. A match that times out counts
as "no match" and is noted in the trace; the run continues with the next rule.

**Validation** (`RuleValidator`). Errors: missing name, no conditions (a rule without conditions
would match everything), no actions, empty texts, invalid or overlong regex, negative unsigned
amount, missing or reversed `between` bounds, empty account/source sets, open or reversed date
range, empty tag/payee/append text, empty ids, split shape (at least two lines, positive amounts
except the last, last without amount, positive percentages summing to 100), and, with a
`RuleValidationContext`, unknown categories or accounts. Warnings: an unused upper bound, and two
actions setting the same field in one rule.

**Suggester** (`RuleSuggester.FromTransaction`). Conditions: normalized raw descriptor equals the
transaction's (robust to store numbers and processor noise) and the same direction. Actions:
rename when the payee name differs from the descriptor, transfer account, splits as fixed amounts
(last line takes the rest) or the category, and the tags. Enabled, stop at first match, never
auto-approving; the user reviews it in the editor.

## Consequences

- The persistence task stores `RuleJson.Serialize(...)` output and reads with
  `RuleDefinition.FromEntity`; a `RuleFormatException` should show the rule as unreadable rather
  than drop it.
- New condition or action kinds can be added under version 1 (old apps refuse them with a clear
  error); changing the meaning of an existing kind needs version 2 and a converter.
- Retroactive apply uses `CompiledRuleSet.ApplyAll` for the preview and writes only
  `RuleMutations.Changes` fields.
