# Changelog

All notable changes to Keel are recorded here, one entry per milestone (PRD 12). Each entry lists
the commands used to demonstrate the exit criteria and their results.

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
