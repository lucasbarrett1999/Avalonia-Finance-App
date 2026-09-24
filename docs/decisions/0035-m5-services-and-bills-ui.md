# 35. M5 services and UI: scheduling anchors, detection runs, alerts, forecast cache, Bills screen

- Status: Accepted
- Date: 2026-09-24
- Milestone: M5 (services and UI)

## Context

ADRs 0030 to 0033 made the recurrence rules, recurring detection, alerts and the forecast pure
domain code and left several questions to "the service": where a schedule's start date lives, how
often and when detection runs, whether its writes are undoable, how alerts are stored, how the
forecast is cached, and how the PRD 9.4, 9.6 and F-REP-4 screens present these things. This record
states the choices made in `Keel.Infrastructure/{Recurring,Scheduling,Alerts,Forecast}` and the M5
desktop screens. Each point has a test in `tests/Keel.Infrastructure.Tests/Recurring/` or
`tests/Keel.Desktop.Tests/BillsScheduleAlertsTests.cs`.

## Decision

**Scheduled transactions (F-ACC-6).**

1. `ScheduledTransaction` has no start column (ADR 0030). The service stores rules **explicit and
   without COUNT/UNTIL**: the weekday, day of month and month a rule would take from its start date
   are written into it (`FREQ=MONTHLY` from the 31st is stored as `FREQ=MONTHLY;BYMONTHDAY=31`);
   `UNTIL` and the date of the `COUNT`-th occurrence become `EndDate`. `NextDate` is always an
   occurrence, so evaluating from it keeps the `INTERVAL` phase (the fortnight, the quarter) and
   the clamping of short months. No migration.
2. A schedule is finished when `NextDate > EndDate`; it stays listed (with its entered rows linked)
   until the user deletes it. Deleting clears `ScheduledFromId` on entered rows and the recurring
   item's link in tracked rows, so undo restores them.
3. Only a schedule's **next** instance can be entered or skipped; instances are taken in order.
   Entering uses the ledger's own save path (`TransactionService.SaveCoreAsync`, now `internal`),
   so transfers create their pair; both sides get `Source = Scheduled` and `ScheduledFromId`. The
   transaction is dated on the instance date (also for "Enter now" before the date), is approved and
   uncleared. Enter, skip and the day's auto-entry are each one undoable ledger action.
4. Auto-enter schedules are entered for every due date on app start and on day change (at most 400
   instances per schedule per run). The others are listed in a startup prompt (enter, skip, enter
   all, later); "later" asks again on the next start or day change.

**Recurring detection (F-REC-1).**

5. Detection runs on demand ("Run detection now"), once per app day (the last run date is the
   `recurring.lastDetectionDate` setting) and **after every import**. Imports are found without
   touching the import pipeline: after every `LedgerChanged`, `DetectNewImportsAsync` reads the
   `Transaction` rows created in the audit log since a stored watermark (`AuditEvents` rowid) and
   keeps those with `Source` File or Provider; they are the alert batch of the run.
6. Decisions are applied through `LedgerWriter` (audited) but automatic runs are **not on the
   user's undo stack**: undo should revert the user's last action, not a background run. User
   actions on items (confirm, pause, resume, dismiss, edit, create, link) are undoable.
   `LedgerWriter` still publishes `LedgerChanged` (with no accounts) for these commits; the services
   also publish `RecurringChanged`.
7. Input rows are non-deleted, non-transfer, non-system transactions with a payee, grouped by the
   `PayeeNormalizer` output of the payee's **name** (the same text used for stored items). A new
   item takes the payee of its latest occurrence and the most common category of its occurrences.
8. The subscription flag is stored on the item. Designations (category groups and tags, settings
   `recurring.subscriptionGroupIds`/`...TagIds`) classify new items; changing them reclassifies every
   item. The Bills screen designates groups; tags are supported by the service but have no UI yet
   (tags are P1).
9. Re-enabled payees are kept in `recurring.reenabledPayees` until the item comes back; re-enabling
   runs detection at once. Dismissing again removes the payee from the list.

**Alerts (F-REC-3).**

10. Alerts are notifications, not ledger rows: `AlertService` writes them in its own context (no
    undo entry, no `LedgerChanged`) and publishes `AlertsChanged` with the unread count after every
    change. Evaluation during a detection run gets the stored items **as they were before the run**
    (a price change makes the detector mark the item variable, which would otherwise suppress the
    very alert it should raise). `EvaluateAsync` with created item ids rebuilds "create" decisions
    from the stored rows.

**Forecast (F-REP-4).**

11. The floor and the discretionary toggle are the `forecast.settings` data-file setting. The
    Reports accounts filter restricts the projected accounts (a transfer to an unselected account
    then counts one side). Results are cached per (day, request, audit-log position) and dropped by
    `Invalidate()`, which the desktop calls on `LedgerChanged` and `RecurringChanged`; the audit
    position also invalidates writes made while no desktop is listening.

**Screens.**

12. Register ghost rows are a collapsible "Scheduled (n)" strip above the grid, italic, in the
    register's column order, with Enter now / Skip on each schedule's next instance and Edit on
    every row, instead of rows inside the virtualized `DataGrid` (ADR 0010: the grid's source is a
    paged database view; mixing projected rows into it would break paging and running balances).
    They show the next 31 days plus overdue instances; a scheduled transfer also shows in the other
    account's register.
13. The notification center is an in-window panel under the bell (like the dialog layer), not a
    popup window, so it renders in headless tests and behaves the same on every OS; a click outside
    or Esc closes it.
14. The Bills calendar shows paid occurrences (from the ledger), expected ones (Active and Detected
    items) and instances of scheduled transactions not linked to an item. The Home "Upcoming bills"
    card lists outflows in the next 7 days from Active and Detected items (unconfirmed ones are
    labelled) and non-transfer scheduled outflows (overdue included). The forecast card shows the
    account with the lowest projected balance.
15. Days below the floor are listed as runs of consecutive days per account with the run's lowest
    balance; clicking a run explains its lowest day.
16. Shared M1/M6 types were extended without reordering: optional constructor parameters on
    `ShellViewModel`, `AccountsViewModel`, `HomeViewModel` and `ReportsViewModel`; `ReportKind.Forecast`
    and `SupportsRange`/`SupportsTracking`/`RangeTextOverride` on `ReportViewModel`; new
    `LedgerAction` values; appended members on the M5 contracts.

## Consequences

- Detection runs cause one app-wide refresh through `LedgerChanged`; views that only need
  recurring data listen to `RecurringChanged` as well.
- A user edit of a detected item's amount or dates is still overwritten by the next detection run
  (ADR 0031); a "user-locked" flag would need a migration.
- PNG export of reports (PRD 9.8) and a tag designation UI remain open.
