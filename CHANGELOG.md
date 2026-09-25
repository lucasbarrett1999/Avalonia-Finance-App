# Changelog

All notable changes to Keel are recorded here, one entry per milestone (PRD 12). Each entry lists
the commands used to demonstrate the exit criteria and their results.

## M8 — Polish, first-run, backups and packaging

Milestone 8 (PRD 12): the first-run experience (PRD 9.10), data-file management and backups (F-SET-1,
PRD 10), appearance and formats (F-SET-2), the command palette and keyboard reference (F-SET-5, PRD
9.1), the accessibility and polish pass (PRD 8, 9.11, 11), Velopack packaging for three systems, the
user guide, and the carried-over review-queue index and Settings → Bills. Version 1.0.0-rc.1.

### Added

- **Budget-file sessions** (ADR 0080): one host per open file; New, Open, Move and Restore start a
  fresh session and move the window to it, so no screen, cache or undo entry of the previous file
  survives; a file that cannot be opened leaves the current one running. `.keel` paths on the command
  line open the file; single instance per data directory (lock file and local pipe): a second launch
  brings the window forward and hands over its file; macOS document activations take the same path.
- **First-run setup** (ADR 0084): Welcome (create with name and folder, or open existing), starter
  template or empty, first account with balance and currency or "How bank connections work" with the
  bank sync guide and Settings → Connections, then Budget with Ready to Assign equal to the opening
  balance. Home's "Get started" card tracks four steps computed from the file.
- **Settings → General** (ADR 0081): file, data, backups and logs paths; New, Open, Move budget file;
  Back up now (verified zip in `budgets/backups`), automatic daily backups with keep-N, backups list
  with Restore (confirmation, before-restore backup), Restore from a file; backup before migrations;
  the daily `PRAGMA integrity_check` with a status-strip error, Check now; Copy diagnostic bundle
  (logs and a schema-only summary).
- **Settings → Appearance** (ADR 0082): accent (six AA-checked accents), comfortable/compact density,
  motion (follow the OS on Linux and macOS, reduce, full), number/date/currency formats following the
  OS or a chosen region. **Settings → Bills and subscriptions**: category groups and tags that mark
  subscriptions. **Settings → Updates**: Velopack update check, off by default. **Settings → Keyboard
  shortcuts** generated from the registry.
- **Command palette** `Ctrl/⌘+K` (ADR 0085): every page, account, Settings section and action with
  fuzzy search and shortcut hints; the `ShortcutRegistry` behind the palette, the reference and the
  menus; new keys `Ctrl/⌘+,`, `Ctrl/⌘+O`, `Ctrl/⌘+Shift+S`, `Ctrl/⌘+1…7`, `Ctrl/⌘+Q`, page keys on
  Bills, Goals and Reports; in-window menu bar (Windows, Linux) and the macOS native menu with About
  and Preferences in the app menu; About dialog.
- **Accessibility and polish** (ADR 0086): automated audits of screen-reader names, WCAG AA text
  contrast (composed backgrounds) and tabular figures on every screen, report, Bills tab, main dialog,
  the palette, notifications and the first-run steps in both themes; pairwise token and accent
  contrast tests; fixes for accent-button, placeholder, selected-item and selected-text contrast;
  2x (192 DPI) rendering at 960 × 540 (1080p at 200%) with Budget's narrow header and floating
  inspector; loading states for Home and Goals; reduced motion stops the sync spinner; focus order test.
- **Packaging** (ADR 0083): `build/package.sh` and `build/package.ps1` with vpk 1.2.158 (local tool),
  one Velopack channel per RID; Windows Setup.exe and portable zip with per-user `.keel` registration;
  macOS Keel.app with the `.keel` document type, .pkg, zip and .dmg; Linux AppImage and a .deb with
  desktop entry, icons and MIME type; app icon from one SVG (`build/icons/generate-icons.cs`, .ico,
  .icns, PNGs); `.github/workflows/release.yml` on `v*` tags; CI dry-runs the packaging script.
- **Docs**: `docs/user-guide/` (nine pages including the existing bank sync guide), `docs/qa-checklist.md`,
  README with screenshots in `docs/images/`, CLAUDE.md final pass.
- **Carried over**: `IX_Transactions_ReviewQueue` and the 100k review-queue timing test (f6f5c94);
  Settings → Bills subscription designations over `IRecurringService`.

### Fixed

- Budget files in rollback-journal mode (copies, restored files, files from elsewhere) failed to open
  ("attempt to write a readonly database"): read-only connections no longer set the journal mode.
- The first-run currency is USD, not XDR, on machines with an invariant locale.

### Decisions and deviations

- [ADR 0080](docs/decisions/0080-budget-file-sessions.md) budget-file sessions and single instance;
  [ADR 0081](docs/decisions/0081-backups-and-data-file-care.md) backups, restore, integrity check,
  move and diagnostics; [ADR 0082](docs/decisions/0082-appearance-locale-and-motion.md) named accents
  instead of a free picker, format culture applied by reopening, reduce motion read from GNOME/macOS
  only; [ADR 0083](docs/decisions/0083-packaging-and-updates.md) .deb via dpkg-deb and .dmg via
  hdiutil (vpk makes neither), per-user Windows file association from Velopack hooks, unsigned by
  default; [ADR 0084](docs/decisions/0084-first-run-and-setup-checklist.md) when the setup shows and
  how the checklist is computed; [ADR 0085](docs/decisions/0085-command-palette-and-shortcut-registry.md)
  registry, palette and menus; [ADR 0086](docs/decisions/0086-accessibility-audits.md) audits, the
  window minimum height lowered from 560 to 520, narrow Budget layouts.
- SQLCipher (F-SET-4, P1) and the YNAB/Monarch importers (P1) are not in M8.

### Verification (Linux sandbox, .NET SDK 10.0)

| Command | Result |
|---|---|
| `dotnet build Keel.sln -c Release` | 0 warnings, 0 errors |
| `dotnet test Keel.sln -m:1` | 1,788 passed, 4 skipped (gated: Plaid sandbox, Secret Service, Keychain, DPAPI) |
| `dotnet format Keel.sln --verify-no-changes` | clean |
| `dotnet test tests/Keel.Desktop.Tests --filter "FirstRunTests\|DataFileSettingsTests\|CommandPaletteTests\|AccessibilityTests\|TokenContrastTests\|HighDpiRenderingTests"` | all pass |
| `dotnet test tests/Keel.Infrastructure.Tests --filter ReviewQueueTimingTests` | 19,072 unapproved of 100k: count 20 ms, first page 5 ms, last page 38 ms |
| `build/package.sh --rid linux-x64` | `Keel-linux-x64.AppImage` 56.5 MB, `keel_1.0.0~rc.1_amd64.deb` 37.4 MB, update packages |
| `build/package.sh --rid win-x64` (cross-pack from Linux) | `Keel-win-x64-Setup.exe` 65.7 MB, portable zip, update packages |
| `apt-get install ./keel_1.0.0~rc.1_amd64.deb` (Ubuntu 24.04), then `apt-get remove keel` | installs with dependencies, registers the MIME type, removes cleanly |
| `actionlint .github/workflows/*.yml` | clean |

### Exit criteria

- **"A fresh user can complete Section 9.10 in under 15 minutes"**: `FirstRunTests` drives the whole
  flow through the real window (create, template, account, Budget with Ready to Assign = balance,
  checklist to 4 of 4) in about 6 s headless; the human 15-minute measurement is item 2 of
  `docs/qa-checklist.md`.
- **"v1.0.0 tag builds installers in CI"**: `release.yml` is in place and validated with actionlint;
  the Linux and Windows x64 packages were built locally with the same script. Not demonstrated: GitHub
  Actions hosted runners refused all jobs on this account during M8, so neither CI nor the release
  workflow has run, and no tag was created (the version stays 1.0.0-rc.1).

### Not done here

- macOS packaging (Keel.app, vpk osx, .dmg), Windows arm64 packaging, the Windows registry file
  association and the Velopack update flow could not be run on Linux; they run on the release
  workflow's macOS and Windows runners.
- Code signing and notarization need certificates (optional secrets in the release workflow).
- F-SET-4 SQLCipher encryption (P1) and the "Stats" page (PRD 4) are not implemented.

## M7 — Bank sync

Milestone 7 (PRD 12): OS secret stores (PRD 6.7), the Plaid provider with Hosted Link and
`/transactions/sync` (F-TXN-3, PRD 7.5), the sync service over the unified import pipeline,
Settings → Connections (F-SET-3), health states in the shell and register, and SimpleFIN Bridge (P1).

### Added

- **Secret stores** (`Keel.Infrastructure/Platform/`): Windows DPAPI blobs in `secrets/`, macOS
  Keychain through Security.framework (`SecItemAdd/CopyMatching/Update/Delete`, service
  `com.keel.app`), Linux Secret Service over D-Bus (`Tmds.DBus.Protocol`) with an AES-256-GCM file
  fallback keyed by Argon2id over the machine id and user name. `SecretStoreSelector` picks one at
  first use and reports the weaker fallback to Settings (ADR 0070).
- **Plaid provider** (`Sync/Plaid/`, Going.Plaid 6.67.0 behind `IPlaidApi`, `IHttpClientFactory` +
  standard resilience handler): bring-your-own keys and environment from the secret store; Hosted Link
  opened in the system browser with `/link/token/get` polling every 2 s for up to 10 min; token
  exchange into the secret store under the connection's `SecretRef`; update-mode links for
  `ITEM_LOGIN_REQUIRED`; accounts, cached balances, `/transactions/sync` pages, health, `/item/remove`.
- **Sync service** (`ISyncService`, `Sync/SyncService`): link with an account mapping (create, link
  existing with a suggestion, skip); sync one, one account's, or all connections, each page imported
  through `IImportService` (source Provider) with the cursor stored after the page commits; removed
  ids soft-deleted; balances as reported balances and Provider snapshots; new linked accounts start at
  the bank's balance once history is in; reconnect; unlink keeping local transactions; sync settings
  in the `Setting` table (ADR 0071).
- **SimpleFIN Bridge** (P1, `Sync/SimpleFin/`): setup token → access URL claim, `/accounts?start-date=`
  polling with an overlap window, balances and health; credentials only in a Basic header (ADR 0072).
- **Desktop**: Settings → Connections (connection list with health dot, accounts, last sync, Reconnect,
  Sync now, Unlink; masked key and token entry with Replace; environment; the BYO-keys warning; the
  secret store in use with the Linux fallback warning; schedule; "No connections yet" empty state);
  the add-connection dialog (provider, browser, cancellable waiting state with the link shown, account
  mapping); top-bar Sync all with the last-sync time; scheduled sync on open and every N hours; sidebar
  health dots (green, amber, red, with tooltips and screen-reader text) and a syncing spinner; the
  register's Sync button and a reconnect banner on linked accounts; progress and results in the status
  strip (`ViewModels/Sync/`, `Views/Sync/`, `Styles/Sync.axaml`).
- **Docs**: `docs/user-guide/bank-sync.md` (Plaid keys, sandbox `user_good` / `pass_good`, the
  security caveat, SimpleFIN setup, where secrets live). The obsolete MyApp-era
  `docs/PlaidConfiguration.md` and `docs/PlaidTokenManagement.md` are removed (PRD 7.6).
- **Tests**: `FakePlaidServer` (Plaid over HTTP, real Going.Plaid and resilience on top) for every
  F-TXN-3 criterion: link, list, initial sync, incremental sync without duplicates (also after a lost
  cursor), pagination with the cursor stored per committed page, mutation-during-pagination restart,
  `ITEM_LOGIN_REQUIRED` as a reconnect state repaired by update mode, unlink keeping rows, pending to
  posted in place (category kept), modified and removed rows, balances as snapshots, retries, no
  secret or payee in any log line, no token in the database file; secret store round trips (Linux
  fallback for real, real Secret Service behind `KEEL_TEST_SECRET_SERVICE`, DPAPI on Windows,
  Keychain behind `KEEL_TEST_KEYCHAIN`, platform-neutral framing and query tests everywhere); the
  gated Plaid sandbox test (`KEEL_PLAID_CLIENT_ID`, `KEEL_PLAID_SECRET`); headless UI flows
  (`SyncConnectionsTests`) and light/dark renders (`SyncRenderingTests`).

### Decisions and deviations

- [ADR 0070](docs/decisions/0070-os-secret-stores.md): selector, key names, blob formats, DPAPI files
  in the data directory (PRD 7.4) instead of `%LOCALAPPDATA%` (PRD 6.7), `Tmds.DBus.Protocol` pinned to
  0.21.3 (Avalonia's version; 0.90+ would break Avalonia on Linux), Konscious Argon2id.
- [ADR 0071](docs/decisions/0071-bank-sync-service.md): connection ids, page and cursor order,
  removals, balances, starting balance for new accounts, connection state not audited, unlink.
- [ADR 0072](docs/decisions/0072-plaid-and-simplefin-providers.md): Plaid request shapes, update-mode
  completion, payee field, cached balances, error mapping; SimpleFIN cursor-as-time and overlap.
- Appended to the M0 contracts: `LinkSession.OpensBrowser`, `LinkResult.ExternalItemId`,
  `SyncResult.IsHistoryComplete`. `Program.CreateHost` gained an overload with a `configure` callback
  (tests swap the secret store and providers).

### Verification (Linux sandbox, .NET SDK 10.0.401)

| Command | Result |
|---|---|
| `dotnet build Keel.sln -c Release --no-incremental` | 0 warnings, 0 errors |
| `dotnet test Keel.sln -m:1` | 1,513 passed, 0 failed, 4 skipped: Domain 1,032; Infrastructure 407 (+4 skipped: DPAPI, Keychain, Secret Service, Plaid sandbox); Desktop 74 |
| `dotnet format Keel.sln --verify-no-changes` | Exit code 0 |
| `dotnet list Keel.sln package --vulnerable --include-transitive` | No vulnerable packages |
| `dbus-run-session` + GNOME Keyring 46.1, `KEEL_TEST_SECRET_SERVICE=1 dotnet test ... --filter SecretStoreTests` | 13 passed, 2 skipped (DPAPI, Keychain): the real Secret Service round trip and the selector choosing it |
| `KEEL_SCREENSHOT_DIR=... dotnet test tests/Keel.Desktop.Tests --filter SyncRenderingTests` | 12 PNGs (Connections empty and with a broken connection, register reconnect banner, add-connection choose and waiting, account mapping; light and dark) reviewed by eye |

### Not done here

- **M7 exit is not claimed.** The Plaid sandbox test ran skipped: no sandbox keys are available in
  this environment (the sandbox host is reachable). Windows DPAPI and the macOS Keychain compile and
  their platform-neutral parts are tested, but they run only on those OSes (Windows DPAPI in CI; the
  Keychain test needs `KEEL_TEST_KEYCHAIN=1`), and CI runners are currently unavailable.
- Hosted Link was not opened in a real browser here (the windowed app is not launched in the sandbox).
- The Home dashboard's Accounts card does not show health dots yet (Home cards belong to another
  work stream); the register and sidebar do.

## M5 — Bills, alerts, scheduling and forecast UI

Second half of Milestone 5: the services behind the M5 contracts and every M5 screen (F-ACC-6,
F-REC-1..4, F-REP-4, PRD 9.1 bell, 9.2 cards, 9.4 ghost rows, 9.6 Bills). Decisions in ADR 0035.

### Added

- `RecurringService`: detection on demand, once per app day and after every import (audit-log
  watermark), decisions applied through `LedgerWriter` (audited; automatic runs not on the undo
  stack), confirm / pause / resume / dismiss / re-enable / create / edit as undoable actions,
  subscription designations by category group or tag (Setting table), F-REC-2 totals, occurrences
  for the calendar and Home, F-REC-4 "create target" through `IBudgetService`.
- `ScheduledTransactionService`: CRUD with explicit stored rules (COUNT/UNTIL become the end date),
  next-date maintenance, due entry (auto-enter or prompt) creating `Source = Scheduled` rows with
  `ScheduledFromId` through the ledger save path (transfers included), enter / skip one instance,
  undo, rule check with description and next dates.
- `AlertService`: `AlertEvaluator` proposals stored idempotently by key, read / dismiss, unread
  count and `AlertsChanged`. `ForecastService`: engine inputs from cleared balances, schedules,
  recurring items and (toggle) 90 days of outflows; explain per day; floor/toggle settings; cache per
  day invalidated on `LedgerChanged`/`RecurringChanged` and by the audit-log position.
- Bills screen: totals, Calendar (month grid, paid / expected / scheduled amounts, prev / next /
  today), List (sortable DataGrid, status filter), Subscriptions (monthly and yearly totals, group
  designation, price-change history), detail panel (amount history chart and table, detection
  explanation, confirm / edit / pause / resume / dismiss / detect again, create scheduled transaction,
  create target, item alerts, show transactions), "Run detection now", add item dialog, designed
  empty, loading and error states.
- Scheduled transactions: editor dialog with a recurrence builder (daily, weekly with weekdays,
  monthly on a day or Nth weekday, twice monthly, yearly; every N; start; never / on a date / after
  N times) showing the rule in words and the next five dates; register "Schedule" button and ghost
  rows (italic, Enter now / Skip / Edit); startup and day-change prompt for due instances.
- Notification center: top-bar bell with unread badge ("9+" above nine) and an in-window panel,
  newest first, kind icons, dismiss, mark all read, open the item in Bills or the payee in the
  register.
- Reports: "Cash-flow forecast" (per-account and combined 90-day lines, shaded low point marker,
  dashed floor line, floor and discretionary-spend settings, days below the floor as runs, per-day
  "show the math", what was left out and why, CSV export). The toolbar hides the date range and
  tracking toggle for it.
- Home: Upcoming bills (7 days) and Forecast (sparkline of the lowest-balance account, low point,
  days below the floor) replace the placeholders and link to Bills and the forecast report.
- `RecurringJobs` (desktop): scheduled entry, prompt, daily and post-import detection, forecast
  invalidation. Styles/Bills.axaml (light and dark tokens for calendar, pills, ghost rows, panel).
- Tests: 17 Infrastructure tests on real SQLite (`Infrastructure.Tests.Recurring`), 6 headless
  flows (`BillsScheduleAlertsTests`), 2 rendering tests (`M5RenderingTests`, 24 PNGs in light and
  dark, reviewed).

### Commands and results

| Command | Result |
|---|---|
| `dotnet build Keel.sln -c Release` | 0 warnings, 0 errors |
| `dotnet test Keel.sln -m:1` | 1,503 passed, 0 failed: Domain 1,032, Infrastructure 387, Desktop 84 (after merging main with M4 Review) |
| `dotnet format Keel.sln --verify-no-changes` | Exit code 0 |
| `KEEL_SCREENSHOT_DIR=… dotnet test tests/Keel.Desktop.Tests --filter M5RenderingTests` | 2 passed; Bills (calendar, list with detail, subscriptions, empty), schedule dialog, prompt, bell panel, forecast (upper and lower), Home (upper and lower), register ghost rows, each light and dark |

No packages were added. No migration: the M0 schema already had every table.

### Exit criteria

M5 (PRD 12): detector tests on synthetic and fixture series and the forecast golden test were met in
the first half (see "M5 — Recurring, scheduling and forecast"); with this half the M5 scope (F-REC-1..3,
F-ACC-6, F-REP-4 engine and report, notification center) is implemented and tested. The M6 exit
"dashboard cards live" now holds for every card.

### Not done here

- No UI to designate subscription tags (the service supports them; tags are P1) and no PNG export
  of reports (PRD 9.8, also open in M6).
- A user edit of a detected item's amount or dates is overwritten by the next detection run (ADR 0031).
- Windows and macOS were not run locally (GitHub-hosted runners were unavailable).

## M2 — Budget screen

UI half of Milestone 2: the Budget screen (PRD 9.3, F-BUD-1..5, F-BUD-7 category notes, F-BUD-8
starter templates) on top of the M2 engine and the M1 ledger.

### Added

- **Budget screen** (`Views/BudgetView`, `ViewModels/Budget/`): month header with previous/next
  (`Alt+←/→`), a jump-to-month picker and Today; the Ready to Assign pill (green ≥ 0, red < 0 with
  the banner "You've assigned more than you have."), month totals and "assigned in later months";
  Fund targets (`Ctrl/Cmd+Shift+F`), Move money, Undo, Manage categories and the inspector toggle.
- **Grid**: collapsible group rows with totals and category rows with Name (target badge: "Needs
  $X" / "Funded"), Assigned (inline `MoneyTextBox`; Enter saves and moves down, Tab/Shift+Tab save
  and edit the next/previous row, Esc cancels, typing a digit starts editing), Activity (opens All
  Accounts filtered to the category and month; card payments open the card's register) and the
  Available pill (green, gray, yellow credit-overspent with a card icon, red cash-overspent with a
  warning icon). Arrow keys move a cell cursor. Credit Card Payment rows show the card balance and
  what is not yet covered (6.4.5). Custom hierarchical grid instead of TreeDataGrid (ADR 0040).
- **Data flow**: one `LoadLedgerAsync` for 12 months back and 3 ahead, all months computed once and
  switched in memory; `BudgetChanged` recomputes from assignments only, `LedgerChanged` reloads
  (lazily when the page is hidden); rows update in place (ADR 0041).
- **Move money** (F-BUD-3): `M` dialog (from/to including Ready to Assign, amount; overspent rows
  preset to cover the overspending) and dragging an Available pill onto another row or onto Ready
  to Assign. Undoable.
- **Inspector** (`I`): "How is Available computed?" (carry, assigned, activity per account, card
  spend and covered per card, overspending) or "How is Ready to Assign computed?" when no category
  is selected; target editor for the four F-BUD-4 types with needed-this-month, underfunded and
  monthly need; quick-assign buttons (F-BUD-5, also a row context menu and the `Q` palette); category
  note and, with Ready to Assign, the month note (F-BUD-7); six-month Available sparkline
  (`Controls/BudgetSparkline`). `T` opens the target editor.
- **Manage categories** dialog (F-BUD-1): add, rename, reorder, hide/show and delete groups and
  categories; deleting something with history asks for a replacement; system groups read-only.
  Empty state offers it plus four starter templates (F-BUD-8: Simple, Detailed, Student, Family).
- **Undo for budget actions**: assign, move money, fund targets and target changes join the session
  undo stack; undo publishes `BudgetChanged` (ADR 0041). Category management actions are undoable
  ledger actions (ADR 0042).
- **Services** (append-only): `IBudgetService.LoadLedgerAsync` and `GetRangeAsync`/`ExplainAsync`/
  `GetQuickAssignAsync` overloads over `BudgetLedgerData`; `ICategoryService` management, usage,
  notes and templates; `IBudgetService` month notes (a `Setting` row per month, undoable); new
  `LedgerAction` and `LedgerError` values. `RegisterNavigation` gained an
  optional category filter. Budget shortcuts are in `PlatformShortcuts` and Settings > Keyboard shortcuts.
- **Tests**: `BudgetTests` (headless): 6.4.7 numbers and pill colours through the real services
  (Groceries −50 yellow, Pay_Visa 300 with $50 not yet covered, RTA 1,100); assign in a cell and see
  Ready to Assign change; Enter/Tab/Shift+Tab/Esc and arrow navigation; move money with the dialog
  and undo; drop a pill on a row; target → underfunded badge → fund targets; month switching with the
  keyboard and the picker, negative RTA banner; Activity opens the filtered register; in-place refresh
  after a ledger change; empty state and templates; manage categories; delete with replacement; quick
  assign from the context menu and the palette; inspector breakdown and notes; load error and retry;
  shortcut registry; clicking another cell saves the typed amount; month switch over the 100k fixture.
  `BudgetUndoAndLedgerDataTests` and `CategoryManagementTests` (infrastructure). `RenderingTests`
  renders the budget grid, the Ready to Assign breakdown, the move-money and manage-categories
  dialogs and the month picker in light and dark.

### Decisions and deviations

- [ADR 0040](docs/decisions/0040-budget-grid-without-treedatagrid.md): TreeDataGrid 11.2+ needs a
  commercial licence (build error AVLIC0001), so the grid is a purpose-built flattened row list.
- [ADR 0041](docs/decisions/0041-budget-undo-and-loaded-ledger-data.md): budget undo entries and
  recomputing over loaded ledger data.
- [ADR 0042](docs/decisions/0042-category-management-rules.md): protected system rows, what
  "history" is, and what moves to the replacement category.
## M6 — Reports, goals and dashboard (part 1)

First part of Milestone 6: reports, goals and the Home dashboard. The forecast report (F-REP-4) and
the dashboard's upcoming-bills and forecast cards need recurring detection and the forecast engine
(M5) and follow in part 2.

### Added

- **Report queries** (`IReportService`, `Keel.Infrastructure/Reports/ReportService`): spending by group
  and category with the previous period (F-REP-1), income versus expense per month with net (F-REP-2),
  and month-end net worth with a per-account breakdown (F-REP-3) where tracking accounts use the latest
  balance snapshot on or before each point plus later activity (F-ACC-7). Transfer, tracking-account and
  account filters. Pure helpers in `Keel.Domain/Reports` (`ReportPeriod`, `BalanceSeries`, `GoalProjection`).
- **Reports screen** (PRD 9.8): report list, shared toolbar (range presets and custom dates, accounts
  filter, include transfers and tracking toggles, Export CSV of the report table), Spending donut that
  drills from groups to categories to the All Accounts register filtered by category and range, Income vs
  expense bars with a net line and table, Net worth line with optional stacked account areas. Every
  slice, bar, point and table row drills to the underlying transactions.
- **Chart styling**: `Styles/Charts.axaml` with one categorical palette (light and dark steps, validated
  for colour-vision deficiency) and chart chrome; LiveCharts 2.0.5 (`LiveChartsCore.SkiaSharpView.Avalonia`).
- **Goals** (F-GOAL-1, PRD 9.7): `IGoalService` over the category and budget services; cards with progress
  ring, monthly need, projected completion at the three-month average pace and a live "what if I added
  $X/month" slider; a two-step "New goal" wizard (name, amount, date, optional linked tracking account)
  that creates the category in a "Goals" group and its savings-balance-by-date target.
- **Home dashboard** (F-DASH-1, PRD 9.2): Ready to Assign (Assign opens Budget), review queue count
  (Start review opens Review), accounts overview by group, top five overspent/underfunded categories,
  12-month net worth sparkline, and designed "available after recurring detection" cards for upcoming
  bills and the forecast. Two columns, one when narrow; refreshes on `LedgerChanged` and `BudgetChanged`.
- `RegisterNavigation` takes an optional category filter (report drill-down).
- **Tests**: report rules on hand-built ledgers (system rows, splits, deleted rows, refunds, both
  toggles, account filter, previous period, snapshot rule), spending equals budget activity on the
  fixture, 100k timings, goal service, domain math; headless flows for every report's drill-down
  (including a real click on a donut slice), CSV export, the goal wizard and card, dashboard numbers
  against the budget service and live refresh, responsive layout; light and dark renderings of Home,
  each report and Goals (reviewed).

### Decisions and deviations

- [ADR 0060](docs/decisions/0060-report-rules-and-drill-down.md): what counts as spending and income,
  the transfer toggle, previous period, net-worth points and the snapshot rule, goal pace, drill-down
  targets (an expense bar opens the Spending report for its month; the Uncategorized bucket opens the
  register for the range only), chart settings, CSV format; PNG export not done.

### Verification (Linux sandbox, .NET SDK 10.0.401)

| Command | Result |
|---|---|
| `dotnet build Keel.sln -c Release --no-incremental` | Build succeeded, 0 warnings, 0 errors |
| `dotnet test Keel.sln -c Release --no-build` | 694 passed, 0 failed, 0 skipped: Domain 350, Infrastructure 296, Desktop 48 |
| `dotnet format Keel.sln --verify-no-changes` | Exit code 0 |
| `dotnet test tests/Keel.Desktop.Tests -c Release --filter Month_switch --logger "console;verbosity=detailed"` | 100k-transaction fixture: first budget load 0.9–1.5 s; month switch (view model + layout) median 30–50 ms over several runs (single outliers up to about 220 ms on a loaded machine); headless software rendering of the frame afterwards about 50–80 ms |
| `KEEL_SCREENSHOT_DIR=/tmp/keel-shots dotnet test tests/Keel.Desktop.Tests --filter RenderingTests` | BudgetGrid, BudgetReadyToAssign, BudgetMoveMoney, BudgetManageCategories, BudgetMonthPicker and the empty Budget screen reviewed in light and dark |

### Not done here

- The three-month view and Flex mode (P1, F-BUD-2/F-BUD-6).
- Rules' JSON is not rewritten when a category is deleted (M4, ADR 0042).
| `dotnet build Keel.sln -c Release` | Build succeeded, 0 warnings, 0 errors |
| `dotnet test Keel.sln -c Release --no-build` | 699 passed, 0 failed, 0 skipped: Domain 362, Infrastructure 296, Desktop 41 |
| `dotnet format Keel.sln --verify-no-changes` | Exit code 0 |
| `dotnet test tests/Keel.Infrastructure.Tests --filter Report_queries_over_100k` | Best of 3 at 100k: spending 12 months 75 ms, all history 195 ms; income vs expense 43 / 208 ms; net worth all history 78 ms |
| `KEEL_SCREENSHOT_DIR=… dotnet test tests/Keel.Desktop.Tests --filter ReportRenderingTests` | 18 PNGs (Home, Home lower half, Spending, Spending drill, Income vs expense, Net worth, Net worth by account, Goals, New goal dialog; light and dark), reviewed |

### Not done here

- Forecast report (F-REP-4) and the dashboard's upcoming-bills and forecast cards (need M5 services).
- PNG export of charts (PRD 9.8); sync health dots on the accounts card (M7).
- Windows and macOS runs happen in CI only.

## M2 — Budget engine

Domain half of Milestone 2 (the Budget screen is a later task).

### Added

- **`BudgetCalculator`** (`Keel.Domain/Budgeting`): PRD 6.4 as a pure function of accounts, groups,
  categories, activity pre-aggregated by (category, month, account), assignments and transfers into
  credit accounts. Per month: Ready to Assign (with "assigned in future months" and prior cash
  overspending), totals, group rows; per category: Assigned, Activity, Carry, RawAvailable,
  Available, CashOverspent, CreditOverspent; per Credit Card Payment category: Covered (per spending
  category), Payments (per paying account), Activity, Available. Proportional Covered allocation
  with the rounding remainder on the largest card, refunds floored at 0, tracking transfers as
  categorized outflows, on-budget transfers ignored, closed accounts included. Every cell and every
  Ready to Assign has an explain breakdown whose terms sum to the number (principle 7, 9.3).
- **`TargetCalculator`** (four target types, underfunded, monthly need) and **`QuickAssign`**
  (F-BUD-5 values).
- **`BudgetAggregationQuery`** (`Keel.Infrastructure/Budgeting`): raw SQL `GROUP BY` over
  non-deleted transactions and splits (a split parent contributes nothing), card payments per
  (card, source, month), monthly card balances; no transaction row is loaded.
- **`BudgetService`** implementing the extended `IBudgetService`: `GetMonthAsync`, `GetRangeAsync`,
  `ExplainAsync`, `AssignAsync`, `MoveMoneyAsync` (Ready to Assign on either side), target CRUD,
  `FundTargetsAsync`, `GetQuickAssignAsync`. Mutations write `AuditEvent` before/after JSON and
  publish `BudgetChanged`. Registered in `AddKeelInfrastructure` (plus `TimeProvider.System`).
  Budget DTOs gained carry, hidden flag, kind, card-payment details (card balance and difference),
  target need fields, assigned-in-future and uncategorized totals.
- **Tests**: Verify golden files for 6.4.7 (including the "$50 after the fact" addition) and 6.4.8
  exactly as written, 14 edge-case goldens (refund larger than spend, two cards with rounding,
  payment larger than covered, cash advance, activity without assignment, future assignments,
  negative RTA, cash + credit overspending, empty month, hidden category, opening balances,
  tracking transfers, month boundaries and leap day, closed accounts); cross-check against a naive
  reference transcription of 6.4 on generated inputs; invariants; range and order independence;
  aggregation against hand-built SQLite ledgers; logged SQL uses `GROUP BY`; service equals the
  calculator on those ledgers; a three-month end-to-end golden through the database.
- **Performance**: deterministic `BudgetInputGenerator`; test asserting `Compute` over 36 months ×
  60 categories × 8 accounts < 200 ms after warm-up; `BudgetCalculatorBenchmarks` (BenchmarkDotNet).

### Decisions and deviations

- [ADR 0007](docs/decisions/0007-budget-calculation-interpretations.md): interpretations of PRD 6.4
  (Covered rounding direction, payment categories overspent = cash, what counts as a payment, cash
  advances, uncategorized activity, month totals include hidden categories, and more).
- [ADR 0008](docs/decisions/0008-budget-service-targets-and-quick-assign.md): target formulas, fund
  targets order and limits, quick-assign definitions, move money, audit format, messages, currency,
  raw SQL for the aggregation.

### Verification (Linux sandbox, .NET SDK 10.0.401)

| Command | Result |
|---|---|
| `dotnet build Keel.sln -c Release --no-incremental` | Build succeeded, 0 warnings, 0 errors |
| `dotnet test Keel.sln -c Release --no-build` | 198 passed, 0 failed, 0 skipped: Domain 135, Infrastructure 49, Desktop 14 |
| `dotnet format Keel.sln --verify-no-changes` | Exit code 0 |
| `dotnet list Keel.sln package --vulnerable --include-transitive` | No vulnerable packages (Verify.Xunit 31.12.5 added to both test projects) |
| `dotnet test tests/Keel.Domain.Tests --filter BudgetPerformanceTests` | Compute 36 × 60 × 8 (17,460 activity rows): median about 15 ms (Release), 18 ms (Debug) |
| `dotnet run -c Release --project tests/Keel.Benchmarks -- --filter '*BudgetCalculator*' --job short` | `ComputeRange` 6.6 ms mean, 5.5 MB allocated; `ComputeLastMonth` 2.7 ms |
| `dotnet test tests/Keel.Infrastructure.Tests --filter Month_switch` | `GetMonthAsync` over 100k transactions: median about 250 ms (aggregation about 170 ms) |

### Not done here

- The Budget screen (PRD 9.3) and its headless tests; month switching < 100 ms at 100k rows needs the
  screen to load a range and switch in memory (ADR 0008).
- Windows and macOS runs happen in CI only.

## [0.1.0-m0] - 2026-09-24 - Milestone 0: reset and skeleton

### Added

- **Repository reset** (PRD 7.6): removed the old MyApp projects, views, view models, scripts,
  settings and migrations. Kept `docs/`. New `.gitignore` for .NET, Avalonia, IDEs and OS files.
- **Solution** `Keel.sln` with `Directory.Build.props` (net10.0, nullable, analyzers),
  central package management in `Directory.Packages.props` at the PRD 7.1 versions,
  `global.json` (SDK `10.0.0`, `rollForward: latestFeature`), `.editorconfig`, and a local tool
  manifest (`dotnet-tools.json`) with `dotnet-ef` 10.0.12.
- **Keel.Domain**: `Money` (long minor units + ISO 4217 currency, checked integer arithmetic,
  banker's-rounding conversions, exact percent and weighted splits, culture-aware
  formatting/parsing), `Currency` (minor-unit digits), entity classes for every PRD 6.2 table,
  the domain enums, and the PRD 6.3 rules in `AccountTypeInfo` (`IsOnBudgetByDefault`,
  `IsCashLike`, `IsLiability`, plus `IsCredit`, `CanOverrideOnBudget`, `GroupOf`).
  `TreatWarningsAsErrors` on.
- **Keel.Application**: interfaces with real signatures (`IAccountService`, `IBudgetService`,
  `IImportService`, `IBankDataProvider` per PRD 7.5, `ISecretStore` per 6.7, `IBackupService`,
  `INavigationService`, `IDataDirectory`, `IBudgetFileService`, `IAppSettingsStore`), DTO records,
  and messenger messages `LedgerChanged` and `BudgetChanged` with `IMessageBus`.
  `TreatWarningsAsErrors` on.
- **Keel.Infrastructure**: `KeelDbContext` mapping the full PRD 6.2 schema with all required
  indexes, ISO-text `DateOnly`, `long` amounts, enums by name, UTC timestamps, soft-delete query
  filter; SQLite WAL, foreign keys, NORMAL sync and busy timeout on every connection;
  `KeelDbContextFactory` and a design-time factory; the `InitialCreate` migration (generated by
  dotnet-ef) seeding the default profile, the Inflow and Credit Card Payments groups and Ready to
  Assign, and enforcing sum(splits) == parent amount (ADR 0003); `BudgetFileService` (creates,
  migrates, refuses files from newer versions); `DataDirectory` (PRD 7.4 paths per OS);
  `JsonAppSettingsStore` (atomic `settings.json`); Serilog rolling file logs in `<datadir>/logs`.
- **Keel.Desktop**: Avalonia 11.3.22 app with Fluent theme (custom teal accent), Inter, compiled
  bindings, generic host with DI and Serilog; convention `ViewLocator`; `NavigationService`;
  the PRD 9.1 shell (collapsible and resizable sidebar with Home, Budget, Review, Bills, Goals,
  Reports, Accounts section and Settings; top bar with file name, search box, undo/redo, Sync all
  and alerts placeholders; status strip); a View + ViewModel for every screen with a designed
  empty state; Settings with theme choice, data locations and a shortcut reference;
  light/dark/system theme and window placement (per display configuration) persisted in
  `settings.json`; shortcuts and their display text from `PlatformHotkeyConfiguration`
  (Ctrl/Cmd+F search, Ctrl/Cmd+Z undo, platform redo, Ctrl/Cmd+B sidebar); UI strings in
  `Strings.resx`. The default `budgets/Default.keel` is created and migrated on first launch.
- **Tests**: Domain (Money arithmetic, rounding, parsing and formatting; account classification;
  entities), Infrastructure (migrations into a temp dir, Account and Transaction round trip, ISO
  date and integer storage, pragmas, required indexes, foreign keys, soft delete, split-sum
  constraint, newer-file refusal, data directory per OS, settings store, logging), Desktop
  headless with Skia (first launch, navigation to every screen, sidebar click, theme toggle and
  persistence, Ctrl/Cmd+F, disabled placeholders, sidebar collapse and window placement, every
  screen rendered in light and dark with no binding or resource errors).
- **Benchmarks**: `Keel.Benchmarks` (BenchmarkDotNet) with a Money arithmetic baseline.
- **CI**: `.github/workflows/ci.yml` builds and tests Release on windows-latest, macos-latest and
  ubuntu-latest; a lint job runs `dotnet format --verify-no-changes` and fails on vulnerable
  packages.
- **Docs**: rewritten `CLAUDE.md` and `README.md`; ADRs 0001 to 0004.

### Decisions and deviations

- [ADR 0001](docs/decisions/0001-record-architecture-decisions.md): record decisions as ADRs.
- [ADR 0002](docs/decisions/0002-ci-workflow-location.md): CI lives in `.github/workflows/`, not `build/`.
- [ADR 0003](docs/decisions/0003-split-sum-constraint-via-triggers.md): split-sum rule via triggers
  and a deferred constraint, because SQLite CHECK constraints cannot use subqueries.
- [ADR 0004](docs/decisions/0004-icons-as-vector-geometry.md): icons are vector geometries;
  `Avalonia.Svg.Skia` stays pinned but unreferenced for now.
- Every package version is exactly as pinned in PRD 7.1. "Latest" entries resolved to
  xunit.runner.visualstudio 4.0.0, Microsoft.NET.Test.Sdk 18.10.1 and Shouldly 4.3.0.

### Verification (Linux sandbox, .NET SDK 10.0.401)

All commands ran after `export PATH=/root/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`.

| Command | Result |
|---|---|
| `dotnet new sln --format sln -n Keel` | Created `Keel.sln` |
| `dotnet new tool-manifest` and `dotnet tool install dotnet-ef --version 10.0.12` | Installed dotnet-ef 10.0.12 |
| `dotnet ef migrations add InitialCreate --project src/Keel.Infrastructure --startup-project src/Keel.Infrastructure --output-dir Persistence/Migrations` | Generated `20260924110824_InitialCreate` (split-sum SQL added by hand, ADR 0003) |
| `dotnet ef migrations has-pending-model-changes --project src/Keel.Infrastructure --startup-project src/Keel.Infrastructure` | "No changes have been made to the model since the last migration." |
| `dotnet restore Keel.sln` | Succeeded |
| `dotnet build Keel.sln -c Release --no-restore --no-incremental` | Build succeeded, 0 warnings, 0 errors (all projects) |
| `dotnet test Keel.sln -c Release --no-build` | 107 passed, 0 failed, 0 skipped: Domain 65, Infrastructure 28, Desktop 14 |
| `dotnet format Keel.sln --verify-no-changes` | Exit code 0 |
| `dotnet list Keel.sln package --vulnerable --include-transitive` | No vulnerable packages in any project |
| `actionlint .github/workflows/ci.yml` | No findings |
| `dotnet run -c Release --project tests/Keel.Benchmarks -- --list flat` | Lists 3 benchmarks (`SumRawMinorUnits`, `SumMoney`, `SplitByPercentages`) |
| `KEEL_SCREENSHOT_DIR=... dotnet test tests/Keel.Desktop.Tests` | 16 PNGs (8 screens x light/dark) reviewed by eye |

### Not verified here

- Windows and macOS builds and tests run only in the CI matrix; they were not run in the sandbox.
- The windowed app was not launched (no display in the sandbox). The headless tests start the
  same host, open the same default file, and render the real window.

## M1 — Ledger

Milestone 1 (PRD 12): F-ACC-1..5, F-ACC-7, payees, `MoneyTextBox`, register virtualization with a
100k fixture generator, basic search (F-TXN-7), undo and audit events. Branch `claude/keel-m1-ledger`.

### Added

- **Domain** (`Keel.Domain/Ledger`): `TransferRules` (the on-budget side of an on/off-budget
  transfer carries the category), `SplitRules`, `ReconciliationMath`, `RunningBalance` (ledger
  order reference), `PayeeNames`, `SearchQuery` (`amount:>100 category:groceries date:2026-08
  payee:"trader"`, free words, ranges) and `MoneyExpression` (locale input with `+ - * /`).
- **Application**: `ITransactionService`, `IRegisterQuery`, `IPayeeService`, `ICategoryService`,
  `IBalanceSnapshotService`, `IUndoService` with `LedgerAction`, `LedgerValidationException` with
  `LedgerError`; `AccountDto` gains opening date, notes and derived uncleared balance.
- **Infrastructure** (`Keel.Infrastructure/Ledger`): every mutation runs in `LedgerWriter` (one
  transaction on the thread pool; an `AuditEvent` with before/after JSON per changed row; a session
  undo entry, 50 deep; `LedgerChanged(accounts, months)` after commit). `UndoService` replays the
  inverse (undo) or the original (redo) of the recorded rows, audited too. Services:
  accounts (create with Starting Balance in Ready to Assign for on-budget assets, uncategorized
  for credit and tracking accounts; a Credit Card Payment category per on-budget card; edit;
  close with balance confirmation; reopen; reorder per group), transactions (add, edit, soft
  delete, restore, purge, cleared toggle, bulk categorize/approve/clear/move, transfer pairs kept
  in sync on edit/retarget/unlink, splits validated before the database constraint, reconciliation
  with balance adjustment and locking), payees (get-or-create by normalized name, ranked
  autocomplete, last category and memo), categories (picker list, minimal create), balance
  snapshots (upsert, latest on or before a date). `RegisterQuery` pages in raw SQL (200 rows):
  filters, sort, search, and running balances from a window sum for the page only. Migration
  `RegisterIndexes` adds two covering register indexes.
- **Fixtures**: `Keel.Infrastructure/Fixtures/LedgerFixtureGenerator`: deterministic ledger (ids
  included) of exactly N transactions (default 100,000): 8 accounts (cash, credit, tracking),
  40 categories in 8 groups, about 90 payees, paychecks, bills, card payments, on/off-budget
  transfers, splits, snapshots, statuses and unapproved rows.
- **Desktop**: sidebar Accounts section grouped Cash/Credit/Tracking with balances and totals
  (closed accounts hidden, "show closed" toggle), account context menu (edit, move up/down), Add
  account and Edit account dialogs (close/reopen), Record balance dialog for tracking accounts;
  the account register and the All Accounts register (Account column) with header balances
  (working, cleared, uncleared, reported with mismatch hint, latest snapshot), filter bar (date
  presets, search, status, category, unapproved only), `DataGrid` over `RegisterSource`
  (virtual `IDataGridCollectionView`, ADR 0010) with date/payee/category/memo/outflow/inflow/
  cleared/running balance, unapproved accent bar, transfer rows, expandable split lines, inline
  add/edit row (payee autocomplete with transfer targets, pre-fill from a known payee, category
  search, split editor with remaining amount), multi-select bulk bar, reconcile bar, designed
  empty, filtered-empty, loading and error states. Keyboard: `N`, `Enter`, `Esc`, `C`, `A`,
  `Ctrl/Cmd+Enter`, `Delete` with an undo toast; `Ctrl/Cmd+Z`, `Ctrl/Cmd+Shift+Z` (and the
  platform redo gesture) and the top-bar undo/redo buttons; global search opens All Accounts
  filtered. `MoneyTextBox` binds `long` minor units. In-window dialog layer, status strip toast.
- **Tests**: Domain ledger rules, search syntax, amount math; Infrastructure service tests on real
  SQLite files (accounts, transactions, transfers, splits, reconciliation, undo/redo, audit,
  messages, payees, snapshots, register paging/sorting/filtering/search/running balance against
  the reference, fixture determinism, 100k timing); headless UI: add via keyboard, save-and-new
  with payee pre-fill, inline edit, `C` toggle, delete + Ctrl/Cmd+Z/redo, reconcile to zero and
  with adjustment, split expansion and transfer rows, 100k All Accounts virtualization, DB
  filtering and header sorting, sidebar and Add account, global search, `MoneyTextBox`; rendering
  of the registers (fixture data), tracking register and Add account dialog in light and dark.
- **Benchmarks**: `RegisterBenchmarks` (open All Accounts, open one account, last page,
  payee-sorted page, search page) over the 100k fixture.

### Decisions and deviations

- [ADR 0009](docs/decisions/0009-register-running-balance.md): the running balance is the ledger
  balance in (Date, Id) order under any sort or filter.
- [ADR 0010](docs/decisions/0010-register-virtual-collection-view.md): custom paged collection view
  (the DataGrid's own view copies its source) and a read-only grid with an inline editor row.
- [ADR 0011](docs/decisions/0011-transfer-payees-and-split-transfers.md): transfer payees are
  synthesized, not stored; split lines cannot be transfers in v1.
- Interpretations (no PRD change): credit-card starting balances are uncategorized (PRD names only
  assets and tracking accounts); reconciled rows refuse amount/date/account edits, deletion and the
  `C` toggle; the on-budget flag can change only before an account has transactions; manual payee
  names are normalized by trimming, collapsing whitespace and upper-casing (import normalization is
  the M3 pipeline's).

### Not in M1

- Saved filters (F-TXN-7) and the register's Import file and Sync buttons (M3, M7) are not built;
  the header shows no placeholder for them. Scheduled ghost rows are F-ACC-6 (P1).
- Account health dots need bank connections (M7).

### Verification (Linux sandbox, .NET SDK 10.0.401)

All commands ran after `export PATH=/root/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`.

| Command | Result |
|---|---|
| `dotnet ef migrations add RegisterIndexes --project src/Keel.Infrastructure --startup-project src/Keel.Infrastructure --output-dir Persistence/Migrations` | Generated `20260924114823_RegisterIndexes` |
| `dotnet build Keel.sln -c Release --no-incremental` | Build succeeded, 0 warnings, 0 errors |
| `dotnet test Keel.sln -c Release --no-build` | 202 passed, 0 failed, 0 skipped: Domain 105, Infrastructure 67, Desktop 30 |
| `dotnet format Keel.sln --verify-no-changes` | Exit code 0 |
| `dotnet test tests/Keel.Infrastructure.Tests --filter LedgerFixtureTests` (100k) | Fixture generated in about 4.2 s; register open (count + summary + first 200 rows) best of 3: All accounts 82 ms, one account (44k rows) 64 ms; last page 124 ms / 73 ms. Asserted < 500 ms. |
| `dotnet run -c Release --project tests/Keel.Benchmarks -- --filter '*RegisterBenchmarks*' --job short` | OpenAllAccounts 86.3 ms, OpenOneAccount 68.0 ms, LastPageAllAccounts 88.0 ms, FirstPageSortedByPayee 292.3 ms, SearchFirstPage 19.5 ms |
| `KEEL_SCREENSHOT_DIR=... dotnet test tests/Keel.Desktop.Tests --filter RenderingTests` | 24 PNGs (screens, registers with fixture data, tracking register, Add account dialog; light and dark) reviewed by eye |

### Not verified here

- Windows and macOS run only in CI. The windowed app was not launched; headless tests drive the
  real window, views and DI graph.
## M3 — Import parsers

Parsing half of Milestone 3 (file import): pure parsers, normalization, deduplication logic,
layout detection, fixtures and tests. The DB-backed pipeline service, CSV mapping dialog and
import preview come in a later task, so the M3 exit criteria are not yet claimed.

### Added

- **Keel.Domain/Import** (PRD 6.5, F-TXN-1 steps 1, 2 and 5): `PayeeNormalizer` with its data
  table `PayeeNoiseTable` (merchant rewrites such as `AMZN MKTP` → `AMAZON`, processor prefixes
  `SQ *`, `TST*`, `PAYPAL *` and 15 more, bank channel phrases, noise words, reference and card
  number patterns); `ImportFingerprint` (SHA-256 of `AccountId|Date|Amount|NormalizedPayee`);
  `JaroWinkler`; `DuplicateMatcher` (provider id, pending to posted, exact fingerprint, fuzzy
  ±2 days / same amount / Jaro-Winkler ≥ 0.85 against Manual and Scheduled rows, insert);
  `TransferDetector` (opposite amounts across accounts within ±3 days, one-to-one, closest first).
- **Keel.Application/Import**: `ParsedTransaction` (implements `IImportRecord`), `ParsedSplit`,
  `IFileImportParser`, `IFileImportParserResolver`, `ImportOptions`, `ParseResult`,
  `DetectedAccount`, `ImportWarning`/`ImportWarningCode`, `CsvColumnMapping`,
  `DetectedCsvLayout`. The M0 `IImportService` stub is unchanged.
- **Keel.Infrastructure/Import**: encoding sniffing (UTF-8 BOM, UTF-16 LE/BE with or without
  BOM, strict UTF-8, Windows-1252 or a declared code page); amount parsing (`$`, codes,
  thousands separators, decimal comma, parentheses, trailing minus, `CR`/`DR`); date formats
  ISO, `yyyyMMdd`, `MM/dd/yyyy`, `dd/MM/yyyy`, two-digit years, `MMM d, yyyy`, `d MMM yyyy`
  with whole-column disambiguation and an ambiguity report; `CsvImportParser` on CsvHelper
  (delimiter sniffing, header detection after preambles, headerless files, signed amount,
  debit/credit, amount + type layouts, sign-convention guess, pending status, per-row
  currency, deterministic explicit mapping); `OfxImportParser` (OFX 1.x SGML and 2.x XML, QFX,
  bank and credit-card statements, several statements per file, `FITID`, `DTPOSTED` with
  time-zone suffix, `TRNAMT`, `NAME`/`PAYEE`/`MEMO`, `CHECKNUM`, `TRNTYPE`, `CURDEF`,
  `LEDGERBAL`/`AVAILBAL`, missing end tags, entities); `QifImportParser` (`Bank`, `CCard`,
  `Cash`, `Oth A`, `Oth L`, `!Account` blocks, `D` date variants, `T`/`U`, `P`, `M`, `N`, `C`,
  `L`, `A`, `S`/`E`/`$` splits); `FileImportParserResolver` and `AddKeelFileImportParsers`.
- **Fixtures** (`tests/Keel.Infrastructure.Tests/Import/Fixtures`, each with `.expected.json`):
  CSV `chase-style-credit-card`, `bofa-style-checking` (preamble, running balance),
  `capitalone-style-360-checking` (amount + type, `MM/dd/yy`), `credit-union-debit-credit`
  (debit/credit, `$`, pending, check, UTF-8 BOM), `european-semicolon` (`dd/MM/yyyy`, decimal
  comma, Windows-1252), `wellsfargo-style-headerless`, `discover-style-card`
  (outflow-positive), `savings-month-names` (`MMM d, yyyy`, `CR`, parentheses, trailing minus);
  OFX `bank-sgml` (Windows-1252, CRLF, no `</OFX>`), `creditcard-xml`; QFX `savings`;
  QIF `bank` (splits, `1/ 2'26` dates), `multi-account` (CCard, Cash, Oth L, skipped Invst).
- **Tests**: payee table (every entry), fingerprint vector, Jaro-Winkler reference values,
  every dedup rule and tie-break, transfer pairing, CsCheck properties (re-classifying an
  identical batch after applying the first result inserts nothing; order-independence);
  fixture-driven exact parse results, per-fixture import-twice idempotence, CSV re-parse with
  the detected mapping; amount, date, CSV, OFX, QIF and resolver unit tests.
- **Package**: CsCheck 4.9.1 (test only).

### Decisions and deviations

- [ADR 0005](docs/decisions/0005-import-dedup-matching-details.md): dedup runs as passes, matches
  each existing row once (identical rows need identical counts), pending-to-posted also matches
  a pending row's provider id, fuzzy matching skips rows already matched; normalizer additions.
- [ADR 0006](docs/decisions/0006-file-import-parser-contracts.md): parser contracts, ambiguous
  dates default to month-first plus a warning, CSV mapping by column index, sign heuristics,
  OFX civil dates without time-zone conversion, OFX/QIF leniency rules.

### Verification (Linux sandbox, .NET SDK 10.0.401)

| Command | Result |
|---|---|
| `dotnet build Keel.sln -c Release --no-restore --no-incremental` | 0 warnings, 0 errors |
| `dotnet test Keel.sln -c Release --no-build` | 481 passed, 0 failed: Domain 240, Infrastructure 227, Desktop 14 |
| `dotnet format Keel.sln --verify-no-changes` | Exit code 0 |
| `dotnet list Keel.sln package --vulnerable --include-transitive` | No vulnerable packages |
| 10k-row CSV parse + dedup classification (ad-hoc, Release) | 233 ms parse, 310 ms classify (NFR: 10k rows < 5 s) |

## M3 — Import pipeline

Second half of Milestone 3: the persisted, unified import pipeline (F-TXN-1) and the file import
UI (F-TXN-2). Branch `claude/keel-m3-import-pipeline`. With the parser half, the M3 exit criteria
are met: every fixture (8 CSV layouts, 2 OFX, 1 QFX, 2 QIF) imports completely and idempotently
through the real service and database (`Every_fixture_imports_completely_and_idempotently`).

### Added

- **Application/Import**: `IImportService` implemented and extended (`ImportBatch` with reported
  balance, per-row `ImportRowOverride`s and parse warnings; `ImportPreviewRow` with payee,
  category, include/transfer defaults; `ImportSummary` with rows left out, warnings and the recorded
  balance; `DedupOutcomes` maps the domain `DedupDecision`), `IImportCategorizationHook` +
  `ImportDraft` (the M4 seam for payee rename, rules and learner), `IImportSettingsStore` with
  `RememberedCsvMapping`, `ImportBatchBuilder`, `CsvDateFormats`, `LedgerAction.ImportTransactions`,
  warning codes `ReconciledNotUpdated`, `CurrencyMismatch`, `OtherAccountsInFile`.
- **Infrastructure/Import**: `ImportService` and `ImportPlanner`: PRD 6.5 dedup against the target
  account's rows in the batch window ±3 days plus provider-id lookups (connection-scoped for future
  sync batches), payee resolution (existing payee by normalized name, else a title-cased new one)
  with default categories, hooks (no-op default registered), `TransferDetector` pairing with
  existing imported rows in other accounts as proper transfer pairs, unapproved cleared inserts,
  in-place updates, fuzzy matches marked `HasImportMatch`, OFX `LEDGERBAL` as a Provider
  `BalanceSnapshot` and the account's reported balance. One `LedgerWriter` unit of work per import:
  audited, one undo entry, one `LedgerChanged`. `BulkLedgerInsert` writes the new rows with the
  same snapshots and audit rows. `ImportSettingsStore` (Setting table). Parsers, the service, the
  hook and the store are registered in `AddKeelInfrastructure`. Migration `ImportMatchFlag` adds
  `Transactions.HasImportMatch`.
- **Desktop**: "Import file" in the register header and in the sidebar account menu (All Accounts
  asks for the account); `ImportWorkflow` with the Avalonia storage file picker (csv/ofx/qfx/qif,
  opens in the account's last folder); CSV mapping dialog (prefilled from the remembered mapping
  when the header matches, else detection; date/payee/memo columns, signed, debit/credit or
  amount + type, date format with the month-first/day-first prompt, sign, decimal separator, lines
  to skip, header, live preview of the first 10 rows); preview dialog listing every row with its
  status (New, Duplicate, Matched to existing, Updated, Transfer pair), import and transfer
  checkboxes, live totals, reported balance, warnings, a statement chooser for multi-account files;
  summary toast with Undo. Dialogs may widen the dialog layer (`PreferredMaxWidth`). New icon
  `Icon.Import`; all text in `Strings.resx`.
- **Tests**: Infrastructure (real SQLite): every fixture twice, same CSV twice, OFX FITID dedup and
  update in place, signed / debit-credit / amount + type, ISO / US / EU / `Mon D, YYYY` dates and
  the ambiguous-date answer, fuzzy match to a manual row (once only, survives re-import), transfer
  pairing across two imports and to a tracking account, undo/redo of an import (matched row,
  transfer partner, payees, snapshot restored), all-duplicate import not on the undo stack,
  preview writes nothing and agrees with the import, overrides, payees and default categories,
  hooks, pending to posted, reconciled rows kept, closed/missing account refused, bulk rows stored
  like EF rows, mapping and folder memory, CsCheck property (25 generated batches with manual
  entries, imported twice through the service: no new rows), 10k-row timing. Desktop headless:
  register button to mapping, preview, import, toast and undo; ambiguous dates and live preview;
  mapping memory; every preview status and overrides; sidebar menu and All Accounts chooser;
  `Import_dialogs_render_in_light_and_dark`. Benchmarks: `ImportBenchmarks`.

### Decisions and deviations

- [ADR 0050](docs/decisions/0050-import-match-flag-column.md): stored `HasImportMatch` column.
- [ADR 0051](docs/decisions/0051-import-memory-in-setting-table.md): CSV mapping (with its header)
  and last folder per account in the `Setting` table.
- [ADR 0052](docs/decisions/0052-bulk-insert-for-imports.md): new imported rows go through one
  prepared command with full audit and undo (EF's per-row inserts missed the 10k-row NFR).
- [ADR 0053](docs/decisions/0053-import-pipeline-interpretations.md): payee naming, status and
  approval, dedup edge cases, transfer candidates, preview checkbox meaning, multi-account files,
  reported balance, summary.
- Small appends to M1 files: `LedgerSession.AddWrittenChanges`, `DialogViewModel.PreferredMaxWidth`
  and `DialogService.CurrentMaxWidth`, `AccountsViewModel`/`ShellViewModel` take `ImportWorkflow`.
## M5 — Recurring, scheduling and forecast

Domain half of Milestone 5 (the Bills screen, notification center UI and database-backed
services are a later task).

### Added

- **`RecurrenceRule`** (`Keel.Domain/Scheduling`): RFC 5545 subset for F-ACC-6 (DAILY, WEEKLY with
  INTERVAL, MONTHLY on day D / last day / Nth weekday / twice monthly, YEARLY, COUNT, UNTIL, WKST);
  parser with messages naming the offending part, canonical string form and equality, English
  `Describe` ("Every 2 weeks on Friday", "Every month on the 2nd Tuesday"), lazy
  `Occurrences(start, from, to)`, `NextAfter`, `First`; the 31st and February 29 clamp to short
  months; pure `DateOnly` math.
- **`RecurringDetector`** (`Keel.Domain/Recurring`): PRD 6.6 over (normalized payee, account)
  groups in a 15-month window: gap windows per cadence, ≥ 0.7 fraction, ≥ 3 occurrences (2 for
  yearly), median of the last 6, max($2, 10%) tolerance, `IsVariableAmount`, confidence, anchored
  next expected date and projection rule, lapsed patterns, per-cadence scores for explanations.
  `RecurringSchedule` (anchors, next date, `InferRule` for stored items), `RecurringReconciler`
  (create/update/no-op decisions: one item per group, dismissed items untouched unless re-enabled,
  lapsed patterns end items), `RecurringMath` (monthly/yearly equivalents, F-REC-2 totals, F-REC-4
  set-aside target), `SubscriptionClassifier` (subscription groups or tags).
- **`RecurringStatus.Detected`** appended: new detections await confirmation; only Active items
  are forecast. Stored by name, no migration.
- **`AlertEvaluator`** (`Keel.Domain/Alerts`): price increase (> 5% and > $1 vs the previous
  charge), expected item missing 3+ days, new recurring item, first charge after a $0/trial
  charge; idempotent through `(kind, item, occurrence)` keys stored in `Alert.PayloadJson`.
- **`ForecastEngine`** (`Keel.Domain/Forecast`): F-REP-4 daily balances per on-budget cash account
  and combined for N days (default 90) from the cleared balance, scheduled occurrences (transfers
  on both sides), confirmed recurring items (skipped when a schedule covers the same payee and
  account), optional average daily discretionary spend; overdue occurrences on day 0; lowest
  balance, days below a floor, per-day explain, skipped sources with reasons.
- **Application contracts** (`Keel.Application/{Recurring,Scheduling,Forecast,Alerts}`):
  `IRecurringService`, `IScheduledTransactionService`, `IForecastService`, `IAlertService` with
  DTOs and `RecurringChanged`/`AlertsChanged` messages (implementations come later).
- **Tests** (345 new, all in `Keel.Domain.Tests`): rule parse/description/error tables, occurrence
  tables with short-month, leap-day and RFC 5545 examples, CsCheck properties (strictly increasing,
  within bounds, window slicing, `NextAfter`, round trip); detector for every cadence exact and with
  jitter plus amount noise (150 seeded cases), thresholds, window, grouping via `PayeeNormalizer`,
  refunds and $0 rows, next-date anchoring (early rent, 31st, 30th after February, holiday shift,
  leap day), lapsed; a realistic ledger (biweekly paycheck, rent on the 1st, Netflix with a price
  increase, quarterly insurance, annual domain, variable utility, cancelled gym, irregular coffee
  and groceries not detected) with a Verify golden; labeled generated ledgers; reconciler, math,
  alert rules and idempotence; forecast unit tests and three Verify goldens (with and without
  discretionary spend, double-count guard on and off, floor detection).
- **Performance**: `RecurringFixtureGenerator` (deterministic, 2k payees, ~123k transactions),
  a timing test (< 500 ms; runs in a non-parallel collection) and `RecurringDetectorBenchmarks`.

### Decisions and deviations

- [ADR 0030](docs/decisions/0030-recurrence-rule-subset.md): the supported RFC 5545 subset, start
  date as DTSTART, clamping instead of skipping short months, rejected parts.
- [ADR 0031](docs/decisions/0031-recurring-detection-interpretations.md): $0 rows and minority-sign
  rows are not occurrences, 2-occurrence groups judged for yearly only, semimonthly window 13–18,
  biweekly vs semimonthly decided by schedule residual, anchored next date ("same day of month"),
  lapsed patterns, the `Detected` status, merge and status rules, rounding of totals and targets.
- [ADR 0032](docs/decisions/0032-recurring-alert-rules.md): alert keys, outflow-only price increases,
  variable items skipped, missing for Active items only, $1 trial charges, 90-day trial window.
- [ADR 0033](docs/decisions/0033-cash-flow-forecast-definitions.md): horizon (today + N days), overdue
  occurrences, the double-count guard, the discretionary-spend definition, floor semantics.
## M4 — Rules and learner

Domain half of Milestone 4: the rules engine, the categorization learner and the categorization
pipeline contract, all pure and persistence-free. The Review screen, the DB-backed rule and learner
services and retroactive apply come in a later task, so the M4 exit criteria (review keyboard flow
headless-tested) are not yet claimed.

### Added

- **Keel.Domain/Rules** (F-TXN-4, ADR 0020): `TransactionSnapshot`; `RuleDefinition` with
  versioned `RuleJson` for `Rule.ConditionsJson`/`ActionsJson` (camelCase `type` discriminators,
  newer versions refused with `RuleFormatException`); conditions payee contains/equals/starts
  with/regex (raw or normalized via `PayeeNormalizer`, matching the renamed payee or the raw
  descriptor), memo, amount equals/between/greater/less (magnitude or signed), direction, account
  set, source set, date range, tag, combined with all/any; actions set payee, set category, set
  memo, append memo, add tag, mark approved, flag, split by fixed amounts (last line takes the
  rest) or percentages (banker's rounding, remainder last), set transfer account.
  `RuleEngine.Compile/Apply` (sort order, first match wins unless continue, later rules see
  earlier changes, regex compiled once with a 100 ms match timeout) returns `RuleMutations`
  (result snapshot, changed fields, rule that set each field) and a `RuleTrace`. `RuleValidator`
  (English messages, codes, paths, optional reference checks). `RuleSuggester.FromTransaction`
  for "Create rule from this transaction".
- **Keel.Domain/Categorization** (F-TXN-5, ADR 0021): `CategoryLearner.Train` → immutable
  `LearnerModel` (naive Bayes with Laplace smoothing; exact-payee prior once a payee has 3
  approved examples; payee tokens, log2 amount bucket, account, weekday, direction);
  `Suggest`/`Predict` with confidence, explanation ("Suggested because 12 of 13 past 'TRADER
  JOES' transactions were Groceries" or the driving words), 60% floor, hidden/system categories
  only when the history is exclusively there; `WithExample`/`WithoutExample` (equal to a full
  retrain); `WithCategories`; deterministic count-only JSON (`LearnerModelJson`).
- **Keel.Application/Categorization** (ADR 0022): `ICategorizationEngine` and
  `CategorizationEngine`: rules decide first (learner skipped), then the payee default category at
  0.95 (F-TXN-9), then the learner; suggestions for the review queue and a `CategorizationTrace`.
- **Tests** (Domain.Tests, which now also references Keel.Application): every condition and action,
  ordering/continue/override, disabled and invalid rules, split exactness (CsCheck properties for
  percentages and fixed amounts), invalid and overlong regex, regex timeout, pinned JSON format,
  validator messages, suggester; learner thresholds, restricted categories, explanations,
  determinism, incremental and removal equivalence, JSON round trip and errors, pipeline stages.
  `LabeledHistoryGenerator`: deterministic 24-month history, 64 payees, 30 categories, 1,277
  distinct descriptors for 2,452 transactions, 9 payees split across two categories, refunds, 1%
  inconsistent labels.
- **Benchmarks**: `CategoryLearnerBenchmarks` (`Train100k`, `Suggest`, `WithExample`).

### Decisions and deviations

- [ADR 0020](docs/decisions/0020-rule-format-and-engine-semantics.md): rule JSON format and
  versioning, payee conditions match renamed or raw payee, amounts compare magnitude by default,
  later continuing rules override earlier ones, fixed-amount split remainder, regex limits.
- [ADR 0021](docs/decisions/0021-categorization-learner-model.md): learner model, features,
  smoothing and tempering, the 3-example rule applied to words too, alternatives below 60% for
  the review picker (never written), restricted categories, how the "85% after 200 approvals" exit
  is measured.
- [ADR 0022](docs/decisions/0022-categorization-pipeline.md): pipeline order, what "a rule decided"
  means, payee default at 0.95, existing categories kept, trace contents.

### Verification (Linux sandbox, .NET SDK 10.0.401)

| Command | Result |
|---|---|
| `dotnet ef migrations add ImportMatchFlag --project src/Keel.Infrastructure --startup-project src/Keel.Infrastructure --output-dir Persistence/Migrations` | Generated `20260924123822_ImportMatchFlag` |
| `dotnet ef migrations has-pending-model-changes ...` | "No changes have been made to the model since the last migration." |
| `dotnet build Keel.sln -c Release --no-incremental` | 0 warnings, 0 errors |
| `dotnet test Keel.sln -c Release --no-build` | 715 passed, 0 failed: Domain 350, Infrastructure 329, Desktop 36 |
| `dotnet format Keel.sln --verify-no-changes` | Exit code 0 |
| `dotnet list Keel.sln package --vulnerable --include-transitive` | No vulnerable packages (CsCheck added to Infrastructure.Tests) |
| `dotnet test tests/Keel.Infrastructure.Tests --filter ImportPerformanceTests` | 10,000-row CSV: parse about 200 ms, pipeline about 1.3-1.4 s, total about 1.5-1.6 s (asserted < 5 s); re-import (all duplicates) about 0.45-0.55 s |
| `dotnet run -c Release --project tests/Keel.Benchmarks -- --filter '*ImportBenchmarks*' --job short` | Parse 47.6 ms, import into empty account 911 ms, all-duplicate re-import 329 ms |
| `KEEL_SCREENSHOT_DIR=... dotnet test tests/Keel.Desktop.Tests --filter Import_dialogs_render` | 4 PNGs (mapping and preview dialogs, light and dark) reviewed by eye |

### Not done here

- Windows and macOS run only in CI; the windowed app and the native file picker were not launched
  (tests use a fake picker). Split lines carried by QIF files and file category text are passed to
  hooks as hints but not applied (M4 rules). No keyboard shortcut for "Import file" yet.
- `BudgetPerformanceTests` (M2, 200 ms bound) can fail when all three test assemblies run in
  parallel on a 4-core sandbox; it passes on its own.
| `dotnet build Keel.sln -c Release --no-incremental` | Build succeeded, 0 warnings, 0 errors |
| `dotnet test Keel.sln -c Release --no-build` | 1,046 passed, 0 failed, 0 skipped: Domain 784, Infrastructure 248, Desktop 14 |
| `dotnet test Keel.sln` (Debug) | Same counts, all passed |
| `dotnet format Keel.sln --verify-no-changes` | Exit code 0 |
| `dotnet test tests/Keel.Domain.Tests --filter RecurringPerformance` | Detect over 123,466 transactions / 2,000 payees: median about 80–90 ms (Release and Debug, shared 4-core machine) |
| `dotnet run -c Release --project tests/Keel.Benchmarks -- --filter '*RecurringDetector*' --job short` | `Detect` 19.2 ms mean, 10.3 MB allocated; `ReconcileAgainstEmpty` 0.58 ms |
| `dotnet test tests/Keel.Domain.Tests --filter Accuracy` | 400 of 400 labeled groups correct for each of 3 seeds, 0 false positives |

No packages were added.

### Not done here

- `IRecurringService`, `IScheduledTransactionService`, `IForecastService` and `IAlertService`
  implementations, persistence, nightly scheduling, the Bills screen, the forecast chart and the
  notification center (later M5 task).
- Timing tests are sensitive to other processes on a shared machine: the existing
  `BudgetPerformanceTests` failed once during this work while the sandbox load average was about
  27 on 4 cores; the detector test asserts on the fastest of five runs for that reason.
| `dotnet build Keel.sln -c Release --no-incremental` | 0 warnings, 0 errors |
| `dotnet test Keel.sln -c Release --no-build` | 768 passed, 0 failed: Domain 506, Infrastructure 248, Desktop 14 |
| `dotnet format Keel.sln --verify-no-changes` | Exit code 0 |
| `dotnet list Keel.sln package --vulnerable --include-transitive` | No vulnerable packages (no packages added) |
| `dotnet test tests/Keel.Domain.Tests --filter LearnerAccuracyTests` | Chronological 80/20: top-1 89.8% (empty counts as wrong), coverage 93.9%, confident (≥ 0.9) 386 of 491 at 98.4% precision; random 80/20 seeds 1/7/2026: 90.1/92.4/89.2%, confident precision 98.5/98.4/99.5% |
| same, after N approvals (next 500) | 200: precision 95.4%, coverage 56.2% (accuracy with empties 53.6%); 400: 76.0%; 600: 85.8%; 1000: 89.8% |
| `dotnet test tests/Keel.Domain.Tests -c Release --filter LearnerPerformanceTests` | Train 100k examples (2,514 payees): median about 540 ms; `Suggest` about 62 µs; `WithExample` about 57 µs; JSON 345 KiB, write + read 38 ms |
| `dotnet run -c Release --project tests/Keel.Benchmarks -- --filter '*CategoryLearner*' --job short` | `Train100k` 200.5 ms (87 MB allocated); `Suggest` 17.3 µs (5.9 KB); `WithExample` 16.7 µs (8.7 KB) |

### Not done here

- Review screen (F-TXN-6), rule editor, DB-backed rule/learner services, model caching in `Setting`,
  retroactive apply with preview (the engine's `ApplyAll` is ready for it) and wiring into the import
  pipeline.
- The transaction entity has no flag column; the `flag` action sets `TransactionSnapshot.IsFlagged`.
- The "≥ 85% after 200 approvals" exit is met as accuracy of the suggestions made; counting empty
  suggestions as wrong it is 53.6% at 200 and ≥ 85% from 600 approvals (ADR 0021).
- Windows and macOS runs happen in CI only.

## M4 — Review and rules UI

Second half of Milestone 4: the database-backed rule, learner and categorization services, the
Review screen, the rules UI, payee defaults, and the import hook. With the domain half above, the
M4 exit criteria are met: learner accuracy ≥ 85% after 200 approvals (`LearnerAccuracyTests`, ADR
0021) and the review keyboard flow headless-tested (`ReviewTests`).

### Added

- **Rules service** (`IRuleService`, `Keel.Infrastructure/Rules`): CRUD over `Rule` storing
  `RuleJson`, validation with `RuleValidator` before save (`RuleValidationException`), move up/down
  and drag order, enable/disable, unreadable (newer-format) rules listed and skipped, match count and
  "Test rule" on demand, `PreviewRetroactiveAsync`/`ApplyRetroactivelyAsync(ruleIds, scope)` sharing
  one `MutationPlanner` plan, `SuggestFromTransactionAsync` (`RuleSuggester`). Every mutation is one
  audited, undoable `LedgerWriter` action and publishes `RulesChanged`. The rule "flag" is stored as
  the tag `Flagged`; rule-made transfers create their counterpart row.
- **Learner service** (`ILearnerService`, `LearnerService`): trains from approved history on first
  use, caches the model in the `Setting` table under a version key (`PayeeNormalizer.Version`,
  model format, cache format), stays current by replaying audit events with
  `WithExample`/`WithoutExample` (approvals, recategorization, edits, splits, deletes, undo, payee
  renames), rebuilds on a version change or a stale cache, runs off the UI thread, reports
  `Preparing`/`Ready`, and never writes from `PeekModelAsync`.
- **Categorization service** (`ICategorizationService`): `SuggestAsync`/`SuggestManyAsync` (rule
  first, payee default 0.95, learner with confidence and explanation, full trace), `ApplyRulesAsync`
  (rules write; the learner only suggests), `CategorizeAsync` (full pipeline, never below 60%),
  `ApproveAsync(decisions)`, `PlanBatchApprovalAsync(0.9)`.
- **Import hook**: `RulesImportCategorizationHook` (registered after the no-op default) renames
  payees by rule in step 3 and sets rule categories, payee defaults or learner categories, memos and
  approvals on the drafts in step 4, without writing.
- **Payees** (F-TXN-9 part): `IPayeeService.ListAsync`, `SetDefaultCategoryAsync`, `RenameAsync`
  (retroactive; merges into an existing payee of that name), all undoable.
- **Review screen** (PRD 9.5, F-TXN-6): unapproved transactions across accounts, oldest first,
  50-row pages from `IRegisterQuery`; focused transaction with details, bank descriptor, up to five
  suggestions (source, confidence meter, explanation) and a "Why?" trace; progress "x of y"; "Approve
  N with confidence ≥ 90%"; keys A, 1–9, C (search-as-you-type picker), S (split editor), T (account
  picker), R (prefilled rule editor), D (undo toast), J/K and arrows; loading, "Preparing
  suggestions", error and "Nothing to review" states; keyboard map footer; live sidebar badge.
- **Rules UI**: Settings → Rules and the "Manage rules" page (shared `RulesPanel`): order, drag
  handle, enabled toggle, name, summary, problems, match count, edit, delete with confirmation,
  apply to existing (one rule or all enabled). Rule editor dialog with every condition and action
  kind, live validation per row (errors block saving, warnings do not), "Test rule", and "preview
  applying it after saving". Retroactive preview dialog (scope: all or waiting in Review) with one
  undoable apply. Register context menu "Create rule from this transaction…".
- **Settings → Payees**: search, default category per payee, rename. Review shortcuts listed in
  Settings → Keyboard shortcuts.
- **Tests**: Infrastructure (real SQLite): rule CRUD/order/validation/undo, unreadable rules, match
  count and test, retroactive preview equals apply and one undo reverts it, scoped transfers, learner
  cache build/version key/incremental equals retrain after approvals, edits, splits, deletes, undo
  and payee renames, version and stale-cache rebuilds, categorization order (rule beats payee
  default beats learner), apply-rules vs full pipeline, approvals feed the learner, batch threshold,
  unapproved paging, import through `IImportService` with the hook, payee list/default/rename/merge.
  Desktop headless: review keyboard triage (A, 1, J, K, C, R, D + undo, S, T), badge, batch
  approve, preparing state, rules editor validation, test rule, retroactive preview/apply/undo,
  reorder/toggle/delete, register context menu, payees. `ReviewRulesRenderingTests` renders Review
  (with suggestions and trace), Rules, rule editor, retroactive preview and Settings in both themes.

### Decisions and deviations

- [ADR 0025](docs/decisions/0025-import-categorization-hook-follow-up.md): rules and the learner as
  an `IImportCategorizationHook`; tags, flags, splits and transfers are not expressible on drafts.
- [ADR 0026](docs/decisions/0026-rule-persistence-and-retroactive-apply.md): rule persistence,
  one plan for preview and apply, what the ledger accepts from rules, flag as a tag, payee rename
  merges into an existing name.
- [ADR 0027](docs/decisions/0027-learner-cache-and-incremental-updates.md): learner cache and
  incremental updates by audit-log replay.
- [ADR 0028](docs/decisions/0028-review-queue-triage-semantics.md): what each review key writes,
  batch rules, progress counting.

### Verification (Linux sandbox, .NET SDK 10.0)

| Command | Result |
|---|---|
| `dotnet build Keel.sln -c Release` | Build succeeded, 0 warnings, 0 errors |
| `dotnet test Keel.sln -c Release --no-build -m:1` | 1,478 passed, 0 failed: Domain 1,032, Infrastructure 370, Desktop 76 |
| `dotnet format Keel.sln --verify-no-changes` | Exit code 0 |
| `dotnet list Keel.sln package --vulnerable --include-transitive` | No vulnerable packages (no packages added) |
| `dotnet test tests/Keel.Domain.Tests --filter LearnerAccuracyTests` | 6 passed (M4 exit: ≥ 85% after 200 approvals) |
| `dotnet test tests/Keel.Desktop.Tests --filter "ReviewTests\|RulesUiTests\|ReviewRulesRenderingTests"` | 10 passed (M4 exit: review keyboard flow) |
| `KEEL_SCREENSHOT_DIR=... dotnet test tests/Keel.Desktop.Tests --filter ReviewRulesRenderingTests` | 10 PNGs reviewed by eye |

### Not done here

- Windows and macOS run only in CI (hosted runners were refusing jobs); the windowed app was not launched.
- Rule tags, flags, splits and transfers apply to imported rows only when rules are applied later
  (review approval with the rule, or retroactive apply), not at import (ADR 0025).
- No `IsApproved` index: the unapproved count and first page scan the register index (fine at
  100k rows, not benchmarked separately).
