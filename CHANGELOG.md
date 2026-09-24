# Changelog

All notable changes to Keel are recorded here, one entry per milestone (PRD 12). Each entry lists
the commands used to demonstrate the exit criteria and their results.

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
  note (F-BUD-7); six-month Available sparkline (`Controls/BudgetSparkline`). `T` opens the target editor.
- **Manage categories** dialog (F-BUD-1): add, rename, reorder, hide/show and delete groups and
  categories; deleting something with history asks for a replacement; system groups read-only.
  Empty state offers it plus four starter templates (F-BUD-8: Simple, Detailed, Student, Family).
- **Undo for budget actions**: assign, move money, fund targets and target changes join the session
  undo stack; undo publishes `BudgetChanged` (ADR 0041). Category management actions are undoable
  ledger actions (ADR 0042).
- **Services** (append-only): `IBudgetService.LoadLedgerAsync` and `GetRangeAsync`/`ExplainAsync`/
  `GetQuickAssignAsync` overloads over `BudgetLedgerData`; `ICategoryService` management, usage,
  notes and templates; new `LedgerAction` and `LedgerError` values. `RegisterNavigation` gained an
  optional category filter. Budget shortcuts are in `PlatformShortcuts` and Settings > Keyboard shortcuts.
- **Tests**: `BudgetTests` (headless): 6.4.7 numbers and pill colours through the real services
  (Groceries −50 yellow, Pay_Visa 300 with $50 not yet covered, RTA 1,100); assign in a cell and see
  Ready to Assign change; Enter/Tab/Shift+Tab/Esc and arrow navigation; move money with the dialog
  and undo; drop a pill on a row; target → underfunded badge → fund targets; month switching with the
  keyboard and the picker, negative RTA banner; Activity opens the filtered register; in-place refresh
  after a ledger change; empty state and templates; manage categories; delete with replacement; quick
  assign from the context menu and the palette; month switch over the 100k fixture.
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

### Verification (Linux sandbox, .NET SDK 10.0.401)

| Command | Result |
|---|---|
| `dotnet build Keel.sln -c Release --no-incremental` | Build succeeded, 0 warnings, 0 errors |
| `dotnet test Keel.sln -c Release --no-build` | 689 passed, 0 failed, 0 skipped: Domain 350, Infrastructure 295, Desktop 44 |
| `dotnet format Keel.sln --verify-no-changes` | Exit code 0 |
| `dotnet test tests/Keel.Desktop.Tests -c Release --filter Month_switch --logger "console;verbosity=detailed"` | 100k-transaction fixture: first budget load about 0.9 s; month switch (view model + layout) median about 30–50 ms, max under 105 ms; headless software rendering of the frame afterwards about 50–80 ms |
| `KEEL_SCREENSHOT_DIR=/tmp/keel-shots dotnet test tests/Keel.Desktop.Tests --filter RenderingTests` | BudgetGrid, BudgetReadyToAssign, BudgetMoveMoney, BudgetManageCategories, BudgetMonthPicker and the empty Budget screen reviewed in light and dark |

### Not done here

- Month notes (the per-month half of F-BUD-7; category notes are done), the three-month view and
  Flex mode (P1).
- Rules' JSON is not rewritten when a category is deleted (M4, ADR 0042).
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
