# 31. Recurring detection: interpretations of PRD 6.6 and the merge rules

- Status: Accepted
- Date: 2026-09-24
- Milestone: M5

## Context

PRD 6.6 is normative and `RecurringDetector` (`src/Keel.Domain/Recurring/`) implements its steps
in order. Some steps leave a detail open or conflict with each other, and F-REC-1 asks for
things (confirming a detection) that the M0 schema has no state for. This record states each
interpretation so it can be reviewed and overridden. Every item has a test in
`tests/Keel.Domain.Tests/Recurring/`.

## Decision

**Input and grouping**

1. Groups are (normalized payee, account). The payee is normalized by the existing
   `PayeeNormalizer` (`RecurringTransaction.FromRaw`); there is no second normalizer.
2. The window is the 15 months up to and including the as-of date (`asOf.AddMonths(-15)` onward);
   later-dated rows are ignored.
3. **$0 rows are not occurrences.** They are trial or authorization markers (used by the
   trial-conversion alert) and would add spurious gaps.
4. **Dominant direction.** Within a group only rows of the majority sign are occurrences
   (outflows on a tie), so a refund from a subscription merchant does not break its gaps and
   an occasional reimbursement does not pollute a paycheck. The group key stays (payee,
   account), so there is still at most one item per group as 6.6 step 7 requires.
5. **Yearly with two occurrences.** Step 3 allows yearly items with 2 occurrences but the group
   gate says ≥ 3 transactions in 15 months, which would make an annual renewal (at most two in
   15 months) undetectable. Groups with exactly 2 occurrences are therefore judged for the
   yearly cadence only; everything else needs 3.

**Cadence**

6. Windows (inclusive gap days): weekly 5–9, biweekly 11–17, **semimonthly 13–18**, monthly 25–36
   (30/31 ± 5), quarterly 81–101, yearly 345–385. The PRD gives no semimonthly tolerance; ±2
   around 15/16 covers the 1st/15th (gaps 13–17) and 15th/last-day (13–16) patterns.
7. **Biweekly vs semimonthly.** Their windows overlap on 13–17 days, so for most real series
   both reach the same fraction (a biweekly paycheck has every gap 14, inside both) and "pick
   the highest fraction" cannot decide. When the winner's window overlaps another cadence's
   window and both clear 0.7, the one whose dates fit a fixed-rate schedule better wins: the
   mean absolute residual of the dates against `t0 + k × period` with the phase fitted
   (period 14 vs 365.25 / 24 days). A wrong period drifts ~1.2 days per occurrence, so the
   test is decisive after a few occurrences. Exact ties go to the shorter cadence. Only these
   two windows overlap; for all other cadences the PRD rule applies unchanged.
8. The fraction threshold is ≥ 0.7 (compared with a 1e-9 tolerance so 7/10 passes).

**Amount**

9. Median of the last 6 occurrences, signed; for an even count the mean of the middle two,
   rounded half to even. Tolerance = max($2, 10% of |median|), 10% rounded half to even.
   `IsVariableAmount` when any of those 6 lies outside median ± tolerance. Taken literally,
   a single price change inside the last 6 marks the item variable (confidence × 0.8) until
   the old price leaves the window; we keep this.

**Next expected date (step 6)**

10. Weekly and biweekly: last date + 7 or 14, moved by at most 3 days to the most common weekday
    of the last 6 occurrences (so a holiday-shifted payment does not shift the schedule).
11. Monthly, quarterly and yearly: an **anchor day of month** is chosen from the last 6
    occurrences: the day (or "last day of month") that most of them fall on exactly after
    clamping to short months, ties to the most recent. The 30th stays the 30th after
    February 28; a series on the 31st/30th/28th is "last day". The next date is the anchor day
    nearest to last date + 1, 3 or 12 months (month before, same or after), so a rent paid a
    day early on August 31 is still expected on October 1. Twice monthly: two anchors at least
    8 days apart (circularly, so the 1st and the 31st are neighbours), next = the anchor date
    nearest to last date + 15 days.
12. The detection also carries the `RecurrenceRule` that projects the item from its next
    date (`FREQ=MONTHLY;BYMONTHDAY=1`, `FREQ=WEEKLY;INTERVAL=2;BYDAY=FR`,
    `FREQ=MONTHLY;BYMONTHDAY=15,-1`, `FREQ=MONTHLY;INTERVAL=3;BYMONTHDAY=15`,
    `FREQ=YEARLY;BYMONTH=8;BYMONTHDAY=4`). `RecurringItem` does not store a rule, so
    `RecurringSchedule.InferRule(cadence, next, lastSeen)` rebuilds it from the stored dates:
    the next date's weekday or day, except that a next date on a month's last day is read
    with the last seen date (last day twice = "last day"; otherwise the larger day, e.g. 30);
    twice monthly uses the next and last seen days. A test checks that this equals the
    detection's rule on the realistic fixture.

**Lapsed patterns**

13. A detection is **lapsed** when the as-of date is more than one period plus tolerance past
    the next expected date (a monthly gym last charged in February is lapsed in September).
    PRD 6.6 does not cover stopped patterns; without this, a cancelled subscription would be
    "detected" as new and its missing charges would be forecast forever.

**Confirmation and merge (step 7)**

14. F-REC-1 lets the user confirm detections, and F-REP-4 forecasts "confirmed" items, but
    `RecurringStatus` had no state for an unconfirmed detection. `RecurringStatus.Detected` is
    appended (stored by name, no migration). New patterns are created as `Detected`; the user's
    confirm makes them `Active`.
15. `RecurringReconciler` returns create, update or no-op decisions and writes nothing. It
    matches a detection to the stored item with the same payee and account, else to a stored
    item with the same payee and no account (a manually created item is adopted and gets the
    account). Each stored item matches at most once. Detection updates cadence, amounts,
    tolerance, variable flag, dates and confidence; it never touches the subscription flag,
    category or scheduled-transaction link. An update with no changed field is a no-op, so
    running detection twice writes nothing the second time.
16. Status transitions: Active and Detected keep their status, or become Ended when the
    detection is lapsed; Paused stays Paused; Ended returns as Detected when a newer occurrence
    arrived and the pattern is not lapsed; Dismissed is never touched unless the user
    re-enabled detection for that payee, in which case it returns as Detected (or Ended if
    lapsed). A lapsed detection with no stored item creates nothing.

**Math (F-REC-2, F-REC-4)**

17. Monthly equivalent = amount × occurrences per year (52, 26, 24, 12, 4, 1) / 12, rounded half
    to even per item; totals sum the per-item values so the list column adds up to the total.
    Totals count Active and, by default, Detected items; Paused, Ended and Dismissed never
    count. Outflow totals are negative (the sign convention); subscriptions vs bills split
    outflows only. The F-REC-4 set-aside target rounds the magnitude **up**, so twelve
    set-asides always cover a year of occurrences (yearly $100 → $8.34 per month).
18. Subscription classification takes the categories of user-designated subscription groups
    and designated tags as input (`SubscriptionHints`); where the designations are stored is
    the service's concern.

## Consequences

- The detector reports `Scores` for every cadence, so the Bills detail panel can explain a
  detection ("14 of 14 gaps were monthly").
- A user edit of an item's amount or cadence is overwritten by the next detection run for that
  group. If that proves annoying, a "user-locked" flag on `RecurringItem` is the fix (needs a
  migration).
- `RecurringItem` has no rule column, so a twice-monthly item on unusual days, or one whose last
  payment was jittered on a month-end, projects from inferred days; storing the detection's
  rule would remove the inference.
