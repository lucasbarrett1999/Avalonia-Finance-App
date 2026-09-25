# 102. The local Stats page

- Status: Accepted
- Date: 2026-09-25
- Milestone: M9 (stream E)

## Context

PRD 4 lists five success metrics "instrumented locally, never transmitted" and shown on a private Stats page
in Settings (PRD 9.9: Privacy & Stats). Until M9 nothing measured them. The audit log records row changes
(not actions), import summaries were not stored, and the app had no timing of its own start or of scrolling.
No schema change is allowed in M9 stream E.

## Decision

- **Read-only where possible.** `IStatsService` (Infrastructure `StatsService`) computes on demand:
  - *First launch → first assigned budget*: from the first `BudgetAssignment` audit row with an after-state
    (money assigned) back to `LocalStats.FirstLaunchAt` in settings.json when that is recorded and earlier than
    the file's first audit row, else to the file's first audit row. `FirstLaunchAt` is written only on a fresh
    install (the first-run launch), so upgraded installs honestly count from their file.
  - *Approved without change (30 days)*: the latest audited transaction update per transaction in the window
    that turned `IsApproved` from false to true, for imported and synced rows only (`Source` File or Provider;
    manual entries never went through the categorizer). It counts as unchanged when the category and transfer
    account at approval equal those of the row's creation audit row (what the import pipeline, rules, learner
    or payee default gave it), so a change made in the register before approving counts as a change.
  - *Duplicates on re-import*: see below.
  - *Cold start* and *scroll frame time*: the last samples in settings.json (`AppSettings.Stats`).
- **Import statistics** are recorded by `ImportStatsRecorder`, a decorator around the unchanged `ImportService`
  registered last in `AddKeelInfrastructure`. For file imports it keeps, in the file's `Setting` table under
  `stats.imports` (a data-file setting like ADR 0051's, not audited or undone), a SHA-256 fingerprint of each
  imported batch (account, then date, amount, payee, memo and ids of each row; one-way, so no payee or amount
  is readable) and, when a fingerprint comes back, the rows and `DuplicatesSkipped` of that re-import. The
  metric is flagged rows / rows over all re-imports. Lists are capped (500 fingerprints, 200 re-imports).
  Recording failures are logged and never fail an import.
- **Cold start** (`StatsInstrumentation`, a session service): `Program.Main` hands the process start time
  (`Process.StartTime`) to `BudgetSessions.ColdStartedAt`; the main window, after the sidebar and Home have
  loaded, asks for an animation frame and records process start → that frame, with the file's transaction count
  and whether it is encrypted. Only the first session of a process measures; a locked or failed start is not a
  sample. Tests set `ColdStartedAt` themselves.
- **Scroll frame time.** `RegisterSource` cannot see frames, only row requests, so it got the cheapest possible
  hook: an optional `RowRequested` callback invoked from its indexer. The register view points it at a
  `ScrollFrameMeter`: the first request after a quiet spell starts a loop of `TopLevel.RequestAnimationFrame`
  callbacks, each interval between two frames is a frame time, and the loop ends 250 ms after the last row
  request. At the end of each burst the session's average and 95th percentile are written to settings.json when
  the register has at least 100,000 rows and 30 frames were measured; below that the page says "Not measured".
  Nothing runs while the register is idle.
- **The page** (Settings → Privacy & Stats, `StatsSettingsViewModel`): one card per metric with the value, the
  PRD target, met / misses / not measured shown by icon and words (never colour alone), sample details and a
  plain explanation of how it is computed; loading, error and no-file states; Refresh. Targets: < 15 min,
  > 85%, 100%, < 2 s, p95 ≤ 16.7 ms (60 fps).

## Consequences

- No telemetry: the numbers never leave the machine; the import fingerprints stay in the user's own file.
- The re-import metric starts counting with this version; earlier imports are not fingerprinted.
- The scroll metric measures the frames the window drew while rows were being requested, which includes
  paging in new rows; it is a proxy for jank, not a GPU frame counter.
