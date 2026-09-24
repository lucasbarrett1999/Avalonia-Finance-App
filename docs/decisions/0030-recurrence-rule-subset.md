# 30. Recurrence rules: the RFC 5545 subset and its date semantics

- Status: Accepted
- Date: 2026-09-24
- Milestone: M5

## Context

F-ACC-6 asks for scheduled transactions with "a recurrence rule (RFC 5545 subset: daily, weekly,
every N weeks, monthly on day D or on Nth weekday, yearly, twice-monthly)". The
`ScheduledTransaction.RecurrenceRule` column (PRD 6.2) is a string. RFC 5545 is written for
calendar events with times and time zones and leaves or defines some cases in ways that do not
suit bills: most visibly, `FREQ=MONTHLY;BYMONTHDAY=31` skips every month without a 31st.

## Decision

`Keel.Domain.Scheduling.RecurrenceRule` is an immutable value type with a canonical string form.

**Supported parts.** `FREQ` (DAILY, WEEKLY, MONTHLY, YEARLY), `INTERVAL` (1..999), `BYDAY`,
`BYMONTHDAY` (1..31 and -1..-31), `BYMONTH`, `COUNT`, `UNTIL`, `WKST`. Per frequency:

| FREQ | Allowed | Notes |
|---|---|---|
| DAILY | `BYDAY` plain weekdays (a filter, e.g. weekdays only) | |
| WEEKLY | `BYDAY` plain weekdays, `WKST` | default weekday from the start date |
| MONTHLY | `BYMONTHDAY` or `BYDAY` (with ordinals -5..5, or plain = every such weekday) | default day of month from the start date |
| YEARLY | `BYMONTH` with `BYMONTHDAY` or `BYDAY` | plain `FREQ=YEARLY` takes month and day from the start date |

Rejected with a message naming the part: `BYSETPOS`, `BYWEEKNO`, `BYYEARDAY`, `BYHOUR`,
`BYMINUTE`, `BYSECOND`, HOURLY and finer frequencies, `BYDAY` together with `BYMONTHDAY` (RFC
would intersect them), `BYMONTHDAY`/`BYDAY` in YEARLY without `BYMONTH` (RFC would expand over all
months or weeks of the year), `COUNT` with `UNTIL`, duplicates and unknown parts. Parsing accepts an
`RRULE:` prefix, any letter case, spaces around parts and a trailing `;`. `UNTIL` accepts
`yyyyMMdd` or `yyyyMMddTHHmmss[Z]` and keeps the date. The canonical form orders parts as
`FREQ, INTERVAL (if not 1), BYMONTH, BYMONTHDAY, BYDAY, WKST (if not MO), COUNT, UNTIL`, sorts
list values (month days ascending with negative values last; weekdays with plain entries first,
then ordinals) and is what equality compares.

**Start date = DTSTART.** Every evaluation takes the schedule's start date. It anchors
`INTERVAL` (the fortnight of a biweekly rule is the week of the start date, per `WKST`; the
quarter of an every-3-months rule is the start month), supplies missing weekday, day or month,
and `COUNT` is counted from it. Unlike RFC 5545, the start date is an occurrence only if it
matches the rule (RFC says a non-matching DTSTART makes the set undefined).

**Short months and leap days: clamp, do not skip.** A positive day of month larger than the
month is the month's last day; a negative one beyond the start is the 1st. So the 31st is April
30 and February 28/29, `FREQ=YEARLY` from February 29 falls on February 28 in common years, and
the anchor is never lost (March is again the 31st). Two month days that clamp to the same date
give one occurrence. RFC 5545 skips invalid dates; a rent or card due date does not skip a
month. An ordinal weekday that does not exist in a month (a fifth Friday) is skipped, as in RFC
5545, because there is no natural substitute.

**No clock.** Everything is `DateOnly` arithmetic: no time zones, no DST, no times of day.

**API.** `Occurrences(start, from, to)` is lazy and strictly increasing; `NextAfter(start,
date)` and `First(start)` return null once `COUNT` or `UNTIL` has ended the rule. Evaluation skips
directly to the period that contains `from` when there is no `COUNT`, and ends at year 9999.
`Describe(start?)` renders English text ("Every 2 weeks on Friday", "Every month on the 2nd
Tuesday", "Every month on the 15th and last day", ", 12 times", ", until 2027-12-31").

## Consequences

- A rule written by another RFC 5545 tool that relies on skipped months, `BYSETPOS` (for example
  "last weekday of the month") or year-wide expansion is refused at entry instead of being
  silently reinterpreted.
- `ScheduledTransaction` stores only the rule, `NextDate` and `EndDate`, not the original start.
  A service that advances `NextDate` must keep evaluating with the original anchor for
  `INTERVAL` > 1 and `COUNT` rules; the scheduling service (later task) should persist the start
  (for example in the rule text or a new column) or convert `COUNT` to `UNTIL` when saving.
- Descriptions are English and live in the domain for tests and logs-free diagnostics; the Bills
  and register UI should format them through resources when the app is localized.
