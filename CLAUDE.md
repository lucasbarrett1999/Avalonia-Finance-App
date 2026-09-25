# CLAUDE.md

Guidance for Claude Code (and other agents) working in this repository. Keep it current at
every milestone (PRD 15.10).

Keel is a local-first, cross-platform (Windows, macOS, Linux) personal-finance desktop app:
envelope budgeting plus net worth, recurring bills and forecasts, on one user-owned SQLite
file. The spec is `docs/PRD.md`; read it before changing behaviour. Section 6 (domain model
and budget math) is normative. The build order is PRD section 12; M0–M8 are done (see
`CHANGELOG.md`); the version is 1.0.0-rc.1 and the P1 backlog (M9) is next. User docs are in
`docs/user-guide/`, the release checklist in `docs/qa-checklist.md`.

## Environment

In the agent sandbox the .NET SDK lives in `/root/.dotnet`. Every shell that runs `dotnet`
must first run:

```bash
export PATH=/root/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
```

Only Linux can be verified locally; Windows and macOS are verified by
`.github/workflows/ci.yml`. Headless Avalonia tests need no display. Do not launch the
windowed app in the sandbox. See `docs/build-environment.md`.

## Commands

```bash
dotnet restore Keel.sln
dotnet build Keel.sln                      # Debug
dotnet build Keel.sln -c Release           # what CI builds; must have 0 warnings
dotnet test Keel.sln                       # all test projects (Domain, Infrastructure, Desktop headless)
dotnet test tests/Keel.Domain.Tests        # one project
dotnet test Keel.sln --filter "FullyQualifiedName~MoneyTests"
dotnet format Keel.sln                     # fix formatting; CI runs --verify-no-changes
dotnet run --project src/Keel.Desktop      # run the app (not in the sandbox)
dotnet run -c Release --project tests/Keel.Benchmarks -- --filter '*'
dotnet run -c Release --project tests/Keel.Benchmarks -- --filter '*RecurringDetector*' --job short

# EF Core (dotnet-ef is a local tool: dotnet-tools.json)
dotnet tool restore
dotnet ef migrations add <Name> --project src/Keel.Infrastructure \
  --startup-project src/Keel.Infrastructure --output-dir Persistence/Migrations
dotnet ef migrations has-pending-model-changes --project src/Keel.Infrastructure \
  --startup-project src/Keel.Infrastructure

# Render every screen to PNG (light and dark) for a visual check
KEEL_SCREENSHOT_DIR=/tmp/keel-shots dotnet test tests/Keel.Desktop.Tests --filter RenderingTests

# M1 ledger: headless register flows, 100k register timing, register benchmarks
dotnet test tests/Keel.Desktop.Tests --filter RegisterTests
dotnet test tests/Keel.Infrastructure.Tests --filter LedgerFixtureTests --logger "console;verbosity=detailed"
dotnet run -c Release --project tests/Keel.Benchmarks -- --filter '*RegisterBenchmarks*' --job short
# Regenerate import fixture expectations after a deliberate parser change; review the JSON diff
KEEL_UPDATE_FIXTURES=1 dotnet test tests/Keel.Infrastructure.Tests --filter ImportFixtureTests
# M2 budget screen: headless grid flows (assign, Tab/Enter, move money, drag, targets, fund, months,
# 6.4.7 numbers) and the month-switch timing over the 100k fixture
dotnet test tests/Keel.Desktop.Tests --filter BudgetTests --logger "console;verbosity=detailed"
# M6 reports, goals, dashboard: report query rules and 100k timings, headless flows, screenshots
dotnet test tests/Keel.Infrastructure.Tests --filter "FullyQualifiedName~Reports|FullyQualifiedName~Goals" --logger "console;verbosity=detailed"
dotnet test tests/Keel.Desktop.Tests --filter ReportsGoalsHomeTests
KEEL_SCREENSHOT_DIR=/tmp/keel-shots dotnet test tests/Keel.Desktop.Tests --filter ReportRenderingTests
# M5 bills, scheduling, alerts and forecast: services on real SQLite, headless flows, screenshots
dotnet test tests/Keel.Infrastructure.Tests --filter "FullyQualifiedName~Infrastructure.Tests.Recurring"
dotnet test tests/Keel.Desktop.Tests --filter BillsScheduleAlertsTests
KEEL_SCREENSHOT_DIR=/tmp/keel-shots dotnet test tests/Keel.Desktop.Tests --filter M5RenderingTests
# M3 import pipeline: service tests on real SQLite (with the 10k-row timing), dialogs, benchmarks
dotnet test tests/Keel.Infrastructure.Tests --filter "FullyQualifiedName~Import.Pipeline" --logger "console;verbosity=detailed"
dotnet test tests/Keel.Desktop.Tests --filter "ImportDialogTests|Import_dialogs_render"
# M4 review and rules UI: rule/learner/categorization services on real SQLite, headless review and rules
# flows, screenshots of Review, Rules, the rule editor and the retroactive preview (light and dark)
dotnet test tests/Keel.Infrastructure.Tests --filter "FullyQualifiedName~Tests.Rules|FullyQualifiedName~Tests.Categorization"
dotnet test tests/Keel.Desktop.Tests --filter "ReviewTests|RulesUiTests"
KEEL_SCREENSHOT_DIR=/tmp/keel-shots dotnet test tests/Keel.Desktop.Tests --filter ReviewRulesRenderingTests
dotnet run -c Release --project tests/Keel.Benchmarks -- --filter '*ImportBenchmarks*' --job short
# M7 bank sync: secret stores, Plaid/SimpleFIN providers against fake HTTP, sync service on real SQLite
dotnet test tests/Keel.Infrastructure.Tests --filter "FullyQualifiedName~Sync"
dotnet test tests/Keel.Desktop.Tests --filter "SyncConnectionsTests|SyncRenderingTests"
# Real Plaid sandbox end to end (skipped without keys)
KEEL_PLAID_CLIENT_ID=... KEEL_PLAID_SECRET=... dotnet test tests/Keel.Infrastructure.Tests --filter PlaidSandboxTests
# Real Linux Secret Service: a private session bus with an unlocked GNOME Keyring
dbus-run-session -- sh -c 'printf pw | gnome-keyring-daemon --daemonize --unlock --components=secrets >/dev/null; \
  KEEL_TEST_SECRET_SERVICE=1 dotnet test tests/Keel.Infrastructure.Tests --filter SecretStoreTests'
# M8: first-run, data file, palette/shortcuts/menus, appearance, accessibility audits, 2x rendering
dotnet test tests/Keel.Desktop.Tests --filter "FirstRunTests|DataFileSettingsTests|MaintenanceJobsTests|CommandPaletteTests"
dotnet test tests/Keel.Desktop.Tests --filter "AccessibilityTests|TokenContrastTests|HighDpiRenderingTests|AppearanceTests"
dotnet test tests/Keel.Infrastructure.Tests --filter "FullyQualifiedName~Tests.Files"
KEEL_SCREENSHOT_DIR=/tmp/keel-shots dotnet test tests/Keel.Desktop.Tests --filter M8RenderingTests
# Packaging (vpk is a local tool; Linux needs squashfs-tools; macOS packages only on macOS)
dotnet tool restore
build/package.sh --rid linux-x64            # AppImage + .deb under artifacts/releases/linux-x64
build/package.sh --rid win-x64              # cross-packs Setup.exe from Linux (unsigned)
build/package.sh --dry-run                  # prints every step
pwsh build/package.ps1 -Rid win-x64,win-arm64
actionlint .github/workflows/*.yml
# App icons (checked in): regenerate from Assets/keel-icon.svg only when the design changes
dotnet run build/icons/generate-icons.cs
# M9a: Flex summary goldens and tag service, three-month and Flex view flows (incl. 100k window-shift timing), screenshots
dotnet test tests/Keel.Domain.Tests --filter FlexSummaryTests
dotnet test tests/Keel.Infrastructure.Tests --filter FlexTagTests
dotnet test tests/Keel.Desktop.Tests --filter "BudgetViewsTests" --logger "console;verbosity=detailed"
KEEL_SCREENSHOT_DIR=/tmp/keel-shots dotnet test tests/Keel.Desktop.Tests --filter BudgetViewsRenderingTests
# M9d: CSV export, JSON bundle round trip (and its 100k timing line), YNAB/Monarch importers, UI flows
dotnet test tests/Keel.Infrastructure.Tests --filter "FullyQualifiedName~Portability|FullyQualifiedName~Import.Migration"
dotnet test tests/Keel.Infrastructure.Tests --filter BundleTimingTests --logger "console;verbosity=detailed"
KEEL_UPDATE_FIXTURES=1 dotnet test tests/Keel.Infrastructure.Tests --filter MigrationFixtureTests
dotnet test tests/Keel.Desktop.Tests --filter "PortabilityTests|PortabilityRenderingTests"
KEEL_SCREENSHOT_DIR=/tmp/keel-shots dotnet test tests/Keel.Desktop.Tests --filter PortabilityRenderingTests
# M9c tags, attachments, payee merge: services on real SQLite, headless flows, audits, screenshots
dotnet test tests/Keel.Infrastructure.Tests --filter "FullyQualifiedName~Tests.Tags|FullyQualifiedName~Tests.Attachments|FullyQualifiedName~PayeeMerge"
dotnet test tests/Keel.Desktop.Tests --filter "TagsAttachmentsTests|FullyQualifiedName~AccessibilityTests.Tags|FullyQualifiedName~HighDpiRenderingTests.Tags"
KEEL_SCREENSHOT_DIR=/tmp/keel-shots dotnet test tests/Keel.Desktop.Tests --filter M9cRenderingTests
```

The app applies pending migrations itself when it opens a budget file (after a `-before-migration`
backup); there is no `database update` step.

## Solution map

```
Keel.sln, Directory.Build.props (net10.0, nullable, analyzers, version),
Directory.Packages.props (central package versions, pinned by PRD 7.1), global.json (SDK 10.0)
src/
  Keel.Domain/          Pure domain, no packages. Money + Currency, entities for every PRD 6.2
                        table (Entities/), enums, AccountTypeInfo (PRD 6.3), SystemIds (seeded rows).
                        Ledger/ (M1): TransferRules, SplitRules, ReconciliationMath, RunningBalance (ledger
                        order reference), PayeeNames, SearchQuery (F-TXN-7 syntax), MoneyExpression.
                        Budgeting/: BudgetCalculator (PRD 6.4, pure; input BudgetInput, output BudgetSnapshot
                        with per-cell explain), BudgetMonth, TargetCalculator (F-BUD-4), QuickAssign (F-BUD-5).
                        Reports/ (M6): ReportPeriod (previous period, month-end points), BalanceSeries (ledger plus
                        latest snapshot, ADR 0060), GoalProjection (pace and completion month).
                        Rules/ (F-TXN-4, ADR 0020): TransactionSnapshot, RuleDefinition + versioned RuleJson,
                        RuleEngine/CompiledRuleSet (mutation set + RuleTrace), RuleValidator, RuleSuggester.
                        Categorization/ (F-TXN-5, ADR 0021): CategoryLearner.Train -> LearnerModel (Suggest,
                        Predict, WithExample/WithoutExample, ToJson/FromJson), LearnerCategory, CategorySuggestion.
                        Import/ (PRD 6.5): PayeeNormalizer + PayeeNoiseTable, ImportFingerprint,
                        JaroWinkler, DuplicateMatcher (DedupCandidate/DedupDecision), TransferDetector.
                        Scheduling/: RecurrenceRule (RFC 5545 subset, ADR 0030: Parse/TryParse, canonical ToString,
                        Describe, Occurrences(start, from, to), NextAfter; short months and leap days clamp).
                        Recurring/ (PRD 6.6, ADR 0031): RecurringDetector (RecurringTransaction in, DetectedRecurringItem
                        out), CadenceWindow, RecurringSchedule (anchors, next date, InferRule), RecurringReconciler
                        (create/update/no-op vs stored items), RecurringMath (F-REC-2 totals, F-REC-4 target), SubscriptionClassifier.
                        Alerts/: AlertEvaluator (F-REC-3; idempotency keys in PayloadJson, ADR 0032).
                        Forecast/: ForecastEngine (F-REP-4, ADR 0033; per-account and combined series, explain per day).
  Keel.Application/     Use-case interfaces and DTO records: IAccountService, IBudgetService,
                        IImportService, Sync/IBankDataProvider (PRD 7.5), Security/ISecretStore (6.7),
                        IBackupService, INavigationService, IDataDirectory, IBudgetFileService,
                        IAppSettingsStore/AppSettings, Messaging (IMessageBus, LedgerChanged, BudgetChanged).
                        M1: Ledger/ (ITransactionService, IRegisterQuery + DTOs, LedgerValidationException),
                        Payees/, Categories/, Accounts/IBalanceSnapshotService, Undo/IUndoService + LedgerAction.
                        Budget/: IBudgetService and its DTOs, BudgetDtoMapper (calculator results to DTOs).
                        Import/: ParsedTransaction, IFileImportParser(+Resolver), ImportOptions, ParseResult,
                        ImportWarning, CsvColumnMapping, DetectedCsvLayout.
                        Reports/IReportService (spending, income vs expense, net worth DTOs), Goals/IGoalService (M6).
                        M3 pipeline: IImportService (ImportBatch, IncomingTransaction, ImportPreview(+Row),
                        ImportSummary, ImportRowOverride, DedupOutcomes maps the domain DedupDecision),
                        IImportCategorizationHook + ImportDraft (M4 rules/learner seam), IImportSettingsStore
                        (RememberedCsvMapping), ImportBatchBuilder (ParseResult to batch), CsvDateFormats.
                        Recurring/ (IRecurringService), Scheduling/ (IScheduledTransactionService), Forecast/
                        (IForecastService), Alerts/ (IAlertService): M5 contracts and DTOs (implemented in Infrastructure, M5).
                        Categorization/: ICategorizationEngine + CategorizationEngine (rules, payee default 0.95,
                        learner; CategorizationResult with suggestions and CategorizationTrace, ADR 0022).
                        M4 UI: Categorization/ICategorizationService (suggest, apply rules, full pipeline, review
                        approvals, batch plan) + ILearnerService (Status, GetModelAsync, PeekModelAsync for hooks);
                        Rules/IRuleService (+ RuleDto, RetroactiveScope/Preview/Result, RuleOutcomePreview,
                        RulesChanged, RuleValidationException); Payees ListAsync/SetDefaultCategoryAsync/RenameAsync.
  Keel.Infrastructure/  Persistence/ (KeelDbContext, entity configurations, SQLite pragma interceptor,
                        KeelDbContextFactory, design-time factory, Migrations/), Files/ (DataDirectory,
                        BudgetFileService), Settings/ (JsonAppSettingsStore), Logging/ (Serilog),
                        DependencyInjection.AddKeelInfrastructure.
                        Ledger/ (M1): LedgerWriter (unit of work: audit + undo + LedgerChanged), LedgerSession,
                        EntityChange, UndoHistory/UndoService, AccountService, TransactionService, PayeeService,
                        CategoryService, BalanceSnapshotService, RegisterQuery (raw SQL, paged).
                        Fixtures/LedgerFixtureGenerator (deterministic 100k-transaction ledger).
                        Budgeting/: BudgetAggregationQuery (raw-SQL GROUP BY category, month, account; ADR 0008),
                        BudgetService (IBudgetService: grid, explain, assign, move, targets, fund, quick assign).
                        M2 screen: BudgetService.LoadLedgerAsync + overloads over BudgetLedgerData and budget undo
                        entries (ADR 0041); CategoryService group/category management, notes, templates (ADR 0042).
                        Import/ (pure parsers): TextDecoder, AmountText, DateText, Csv/ (CsvHelper rows,
                        DelimiterSniffer, CsvVocabulary, CsvLayoutDetector, CsvImportParser), Ofx/ (OfxReader,
                        OfxImportParser, also QFX), Qif/QifImportParser, FileImportParserResolver,
                        AddKeelFileImportParsers (not yet wired into AddKeelInfrastructure).
                        Reports/ReportService (raw-SQL GROUP BY report queries, ADR 0060), Goals/GoalService (goals over
                        the category and budget services) (M6).
                        AddKeelFileImportParsers (called by AddKeelInfrastructure since the M3 pipeline).
                        M3 pipeline: ImportService (F-TXN-1 steps 1-7), ImportPlanner (dedup, payees, hooks,
                        transfers; shared by preview and import), BulkLedgerInsert (ADR 0052),
                        NoOpImportCategorizationHook, ImportSettingsStore (Setting table, ADR 0051).
                        M4: Rules/ (RuleService; SnapshotSource = transactions as TransactionSnapshots incl. the
                        "Flagged" tag; MutationPlanner = one plan for retroactive preview and apply, ADR 0026),
                        Categorization/ (LearnerService: Setting-table cache kept current by replaying AuditEvents,
                        ADR 0027; CategorizationService; RulesImportCategorizationHook registered after the no-op, ADR 0025).
                        M5 services (ADR 0035): Recurring/ (RecurringService: detection runs, item actions, totals,
                        F-REC-4 targets, import watermark; RecurringInputs; DataFileSettings + M5Lookups), Scheduling/
                        (ScheduledTransactionService, ScheduleRules: explicit rules anchored at NextDate), Alerts/AlertService,
                        Forecast/ForecastService (per-day cache keyed by the audit-log position).
  Keel.Desktop/         Avalonia app. Program.cs (composition root, generic host), App.axaml,
                        ViewLocator, Views/ (ShellWindow, ShellView, one View per screen),
                        ViewModels/ (ShellViewModel, NavigationItemViewModel, page view models),
                        Controls/ (Icon, EmptyState), Styles/ (Tokens, Icons, Controls, Shell),
                        Services/ (navigation, theme, window placement, shortcuts, startup, DI),
                        Resources/Strings.resx (all UI text).
                        M1: ViewModels/AccountsViewModel (both registers), ViewModels/Register/ (RegisterSource
                        virtual collection view, rows, TransactionEditorViewModel, ReconcileViewModel),
                        ViewModels/Dialogs/ + Views/Dialogs/ (in-window dialogs), SidebarAccountViewModel,
                        Controls/MoneyTextBox, Services/ (DialogService, StatusService, LedgerText), Styles/Register.
                        M2 budget screen: ViewModels/Budget/ (BudgetViewModel: range load + in-memory month switch,
                        row view models and cell cursor, BudgetInspectorViewModel, MoveMoney/QuickAssign/ManageCategories
                        dialogs, BudgetTemplate, BudgetText), Views/BudgetView (custom hierarchical grid, ADR 0040),
                        Views/Budget/ (inspector and dialogs), Controls/BudgetSparkline, Styles/Budget.
                        M6: ViewModels/Reports/ (ReportsViewModel page + Spending/IncomeExpense/NetWorth report view
                        models, ReportFormat incl. CSV), Views/Reports/ (LiveCharts views), ViewModels/Goals/ (GoalsViewModel,
                        GoalCardViewModel, NewGoalViewModel wizard) + Views/Goals/, ViewModels/Home/ (HomeViewModel dashboard,
                        card records), Styles/Charts.axaml (palette, chart chrome, report icons), Controls/ (ChartPalette,
                        ChartSwatch, ChartSparkline, GoalProgressRing, ReportChartKit, ReportIconConverter). The page view
                        models live in those folders but keep the Keel.Desktop.ViewModels namespace (ViewLocator).
                        M3: ViewModels/Import/ (ImportWorkflow, IImportFilePicker + StorageImportFilePicker,
                        CsvMappingViewModel, ImportPreviewViewModel) + Views/Import/ (CsvMappingView, ImportPreviewView);
                        entry points: register header ImportFileButton, sidebar account menu "Import file…".
                        M4: ViewModels/Review/ (ReviewViewModel page in the Keel.Desktop.ViewModels namespace, queue items,
                        suggestions, ReviewSplitViewModel, ShellViewModel.ReviewBadge partial) + Views/ReviewView,
                        Views/Review/; ViewModels/Rules/ (RulesViewModel page "Manage rules", RuleEditorViewModel + parts,
                        RetroactiveApplyViewModel, RuleEditorFlow, PayeesViewModel, Confirm/RenamePayee dialogs,
                        AccountsViewModel.CreateRule partial) + Views/Rules/ (RulesPanel shared by Settings and the page,
                        PayeesPanel, dialogs). Register grid context menu: "Create rule from this transaction…".
                        M5: ViewModels/Bills/ (BillsViewModel page, rows, calendar, detail, RecurringItemEditor,
                        ScheduledTransactionEditor with the recurrence builder, ScheduledPrompt, ScheduledGhosts +
                        ScheduleEditorLauncher) + Views/BillsView, Views/Bills/ (dialogs, BillHistoryChart);
                        ViewModels/Alerts/ + Views/Alerts/ (NotificationCenter under the top-bar bell);
                        ViewModels/Forecast/ForecastReportViewModel (namespace ...Reports) + Views/Reports/ForecastReportView;
                        ViewModels/Home/HomeViewModel.Recurring.cs (Upcoming bills and Forecast cards); Styles/Bills.axaml;
                        Services/RecurringJobs (scheduled entry, daily and post-import detection, forecast invalidation).
tests/
  Keel.Domain.Tests/          xUnit + Shouldly: Money, classification, entities.
  Keel.Infrastructure.Tests/  Real SQLite files in temp dirs: migrations, round trips, pragmas,
                              indexes, soft delete, split-sum constraint, data dir, settings, logging.
                              M1: Ledger/ service tests (LedgerTestHost = real DI over a temp file),
                              register paging/running balance/search, undo, fixture + 100k timing.
                              Import/: parser unit tests and Fixtures/ (bank files + .expected.json).
                              M3: Import/Pipeline/ (ImportKit helpers; every fixture imported twice through the
                              service, F-TXN-2 acceptance, fuzzy match, transfers, undo, mapping memory, CsCheck
                              import-twice property, 10k-row timing in a non-parallel collection).
                              M4: Rules/ (RuleService CRUD/order/undo, retroactive preview = apply, transfers),
                              Categorization/ (learner cache build, incremental = retrain after edits/undo/renames,
                              version rebuild; categorization order; batch plan; unapproved paging; import hook;
                              payee default and rename).
  Keel.Desktop.Tests/         Avalonia.Headless.XUnit with Skia: shell smoke tests, navigation,
                              theme, shortcuts, window state, rendering in light and dark.
                              M1: RegisterTests (keyboard add, inline edit, C, delete + undo, reconcile,
                              100k virtualization), MoneyTextBoxTests, fixture rendering.
                              M2: BudgetTests (BudgetTestLedger = PRD 6.4.7 through the real services), budget
                              rendering with fixture data, dialogs and month picker in both themes.
                              M6: ReportsGoalsHomeTests (drill-downs incl. a real donut click, CSV export, goal
                              wizard, dashboard numbers and refresh), ReportRenderingTests (light and dark PNGs).
                              M4: ReviewTests (keyboard triage A/1/J/K/C/R/D/S/T, badge, batch, preparing state),
                              RulesUiTests (editor validation, test, retroactive preview, reorder, register menu,
                              payees), ReviewRulesRenderingTests (ReviewTestLedger).
                              M3: ImportDialogTests (FakeFilePicker; mapping, preview, register and sidebar entry
                              points, undo from the toast), import dialogs in RenderingTests.
                              M5: BillsScheduleAlertsTests (Bills tabs and confirm, schedule builder to ghost row to entry,
                              startup prompt, bell badge/dismiss/navigate, forecast floor list and explain, Home cards),
                              M5RenderingTests (Bills tabs, empty state, dialogs, bell panel, forecast, Home, ghost rows).
  Keel.Benchmarks/            BenchmarkDotNet (Money baseline; register/calculator/import to come).
                              M1: RegisterBenchmarks over the 100k fixture.
                              M3: ImportBenchmarks (10k-row CSV parse, first import, all-duplicate re-import).
  Keel.Domain.Tests/Budgeting/  Verify golden tests (PRD 6.4.7, 6.4.8, edge cases; *.verified.txt), naive
                              reference cross-check, invariants, 36x60x8 performance test, BudgetInputGenerator
                              (deterministic; also compiled into Keel.Benchmarks for BudgetCalculatorBenchmarks).
  Keel.Domain.Tests/Scheduling/  RecurrenceRule tables (RFC 5545 examples, short months, leap days) and CsCheck properties.
  Keel.Domain.Tests/Recurring/  Detector per cadence with jitter/noise, labeled accuracy, RealisticLedger fixture + Verify
                              golden, schedule/reconciler/math tables, 123k-transaction timing test (TimingCollection runs
                              alone). RecurringSeries/RecurringFixtureGenerator also compiled into Keel.Benchmarks.
  Keel.Domain.Tests/Alerts/, Forecast/  Alert rules and idempotence; ForecastScenario + Verify goldens.
  Keel.Domain.Tests/Rules/     Every condition and action, ordering/continue, split exactness (CsCheck), regex, JSON, validator.
  Keel.Domain.Tests/Categorization/  LabeledHistoryGenerator (64 payees, 30 categories; also in Keel.Benchmarks for
                              CategoryLearnerBenchmarks), accuracy/calibration, determinism, JSON, 100k timing,
                              CategorizationEngine (this project references Keel.Application for it).
  Keel.Infrastructure.Tests/Budgeting/  Aggregation against hand-built SQLite ledgers, BudgetService, 3-month
                              end-to-end golden, 100k-transaction month-switch timing.
  Keel.Infrastructure.Tests/Recurring/  M5 services on real SQLite (M5TestLedger, dates relative to today): detection to
                              items (re-runs, dismissed, re-enable, undo), totals and targets, scheduled entry (auto,
                              prompt, transfers, undo, interval phase), post-import detection, alert idempotence, forecast
                              inputs, cache and settings.
M7 bank sync:
  Keel.Application/Sync/      IBankDataProvider (PRD 7.5), ISyncService + DTOs (PendingConnection, AccountLinkChoice,
                              SyncRunResult, SyncSettings, SyncConnectionsChanged), IBankCredentialsService, SecretKeys,
                              BankProviderException/BankErrorCodes. Security/: ISecretStore, ISecretStoreInfo.
  Keel.Infrastructure/Platform/  SecretStoreSelector, SecretFiles, InMemorySecretStore; Windows/DpapiSecretStore,
                              MacOS/KeychainSecretStore (+KeychainQuery), Linux/SecretServiceStore (D-Bus) and
                              EncryptedFileSecretStore (Argon2id + AES-GCM fallback) (ADR 0070).
  Keel.Infrastructure/Sync/   SyncService, BankCredentialsService, ConnectionSecret, AddKeelSync; Plaid/ (IPlaidApi,
                              GoingPlaidApi, PlaidProvider, PlaidMapping); SimpleFin/SimpleFinProvider (ADR 0071, 0072).
  Keel.Desktop/ViewModels/Sync/  SyncCoordinator (Sync all, schedule, add/reconnect/unlink flows, status strip),
                              ConnectionsSettingsViewModel, AddConnectionViewModel, AccountMappingViewModel,
                              UnlinkConnectionViewModel, AccountsViewModel.Sync (register Sync button, reconnect banner),
                              IBrowserLauncher; Views/Sync/ + Styles/Sync.axaml (health dots, spinner).
  Keel.Infrastructure.Tests/Sync/  FakePlaidServer (HTTP), SyncTestHost, sync/provider/secret-store tests, gated sandbox test.
  Keel.Desktop.Tests/         FakeBankProvider, SyncConnectionsTests, SyncRenderingTests.
M8 polish, first-run, backups, packaging:
  Keel.Application/           Backup/IBackupService (+BackupKind, BackupVerificationException), Files/IDataFileMaintenance
                              (integrity check, copy for move, delete, diagnostic bundle), Setup/ISetupProgressService,
                              Tags/ITagService; AppSettings gains FirstRunCompleted, Accent, Density, FormatCulture,
                              ReduceMotion, AutoBackupEnabled/Keep, CheckForUpdates.
  Keel.Infrastructure/Files/  BackupArchive (zip format, SQLite backup-API copies, verification), BackupService,
                              DataFileMaintenance; BudgetFileService backs up before migrations. Setup/, Tags/.
                              Platform/FileAssociation (.keel type for every OS) + Windows/WindowsFileAssociation (HKCU).
  Keel.Desktop/Services/      BudgetSessions (one host per open file, ADR 0080) + BudgetStartupOptions, SingleInstance +
                              LaunchArguments, MaintenanceJobs (daily integrity check, automatic backup, update check),
                              UpdateService (Velopack, off by default), ShortcutRegistry, AppCommands (palette + menus),
                              MenuBuilder, PageKeys, FuzzyMatch, AppearanceService, LocaleService, FileDialogs, KeelInfo.
  Keel.Desktop/ViewModels/    FirstRun/FirstRunViewModel, Settings/ (DataFile, Appearance, Bills, Updates sections),
                              Dialogs/CommandPaletteViewModel + AboutDialogViewModel, ShellViewModel.M8 (first-run layer,
                              palette, menus, maintenance), Home/HomeViewModel.Setup (Get started checklist).
  Keel.Desktop/Views/         FirstRun/, Settings/, Dialogs/CommandPaletteView + AboutDialogView; Styles/Density.axaml;
                              Assets/ (keel-icon.svg source, PNG, .ico, .icns).
  build/                      package.sh, package.ps1 (Velopack, ADR 0083), icons/generate-icons.cs (+ out/ PNGs).
  Keel.Infrastructure.Tests/Files/  backups, restore, daily keep-N, pre-migration backup, integrity, move copy,
                              diagnostic bundle without data, setup progress, file association.
  Keel.Desktop.Tests/         FirstRunTests, DataFileSettingsTests + MaintenanceJobsTests, CommandPaletteTests (palette,
                              registry vs bindings and the user guide, page keys, menus), AppearanceTests, SingleInstanceTests,
                              BillsSettingsTests, AccessibilityTests + AccessibilityAudit (names, contrast, tabular figures),
                              TokenContrastTests, HighDpiRenderingTests (192 DPI at 960x540), M8RenderingTests, FakeFileDialogs.
docs/  PRD.md, competitive-analysis.md, build-environment.md, decisions/ (ADRs), user-guide/ (README index and
       nine guides incl. bank-sync.md), qa-checklist.md (run before each tag), images/ (README screenshots).
.github/workflows/ci.yml       Build+test on windows/macos/ubuntu, format check, vulnerable-package scan, packaging dry run.
.github/workflows/release.yml  On v* tags: version check, tests, packages on all three OSes, one GitHub release.
M9a: Flex mode and the three-month budget view (ADR 0090, 0091):
  Keel.Domain/Budgeting/FlexSummary.cs  FlexClassifier (tag or default from the target), FlexSummary.Compute over a
                              BudgetMonthResult (buckets = sums of grid cells; card payments in none), FlexBucket, Pace.
  Keel.Application/           BudgetMonthDto.Flex, BudgetCategoryDto.FlexTag/Flex (mapper), ICategoryService.SetFlexKindAsync
                              (+ CategoryDto.FlexKind, LedgerAction.TagCategoryFlex), AppSettings.BudgetThreeMonths/BudgetFlexView.
  Keel.Desktop/ViewModels/Budget/  BudgetViewModel.Views (three-month window: CurrentMonth = cursor month, MonthOffset;
                              Flex view toggle, Flex filter drill-down, tag writes), BudgetMonthCellViewModel (row months),
                              BudgetMonthHeaderViewModel, BudgetFlexViewModel; inspector and Manage categories tag pickers.
  Keel.Desktop/Views/         BudgetView (two row template sets via Views/Budget/BudgetRowTemplateSelector, sideways scroll
                              at BudgetGridSizes.ThreeMonthMinWidth), Views/Budget/BudgetFlexView; Styles/Budget.axaml (M9a).
  Tests: Domain FlexSummaryTests (+ Verify golden on 6.4.7), Infrastructure Ledger/FlexTagTests, Desktop BudgetViewsTests,
                              BudgetViewsRenderingTests; Accessibility and HighDpi tests cover both views.
M9d export, bundle import, YNAB/Monarch importers (F-REP-6, PRD 9.10; ADR 0098-0100):
  Keel.Application/Portability/  IDataExportService (CsvExportFiles, CsvExportResult), IBundleImportService,
                              BundleFormat ("keel-export" v1), BundleInfo, BundleImportResult, BundleException(BundleError).
  Keel.Application/Import/    IMigrationImportService (+ MigrationPlan/Request/Summary, BudgetImportSummary);
                              ImportFileFormat Ynab/YnabBudget/Monarch, ParseResult.BudgetRows/IsMigration;
                              IncomingTransaction.Status/IsApproved/Tags (defaults keep the old behaviour).
  Keel.Infrastructure/Portability/  DataExportService (CSV files + bundle from one ReadSnapshot), CsvTables (column
                              order is the contract), BundleSchema (model-driven tables, AuditEvent and audit-derived
                              Setting keys excluded, value codec), JsonTokenStream (streaming reader), BundleImportService,
                              BulkTableWriter (prepared upsert + audit rows).
  Keel.Infrastructure/Import/ Ynab/ (YnabRegisterParser, YnabBudgetParser), Monarch/ (MonarchImportParser,
                              MonarchCategories), AppExportTable (header-named columns), Migration/
                              (MigrationImportService, MigrationRows, CategoryCatalog). ImportService.ImportCoreAsync is
                              internal static (several batches per unit of work); LedgerWriter.DryRunAsync rolls back.
  Keel.Desktop/               ViewModels/Portability/ (PortabilitySettingsViewModel in Settings → General, ExportDialog,
                              BundleImportDialog, MigrationDialog + rows, BudgetImportDialog, MigrationWorkflow,
                              PortabilityText) + Views/Portability/; FirstRunViewModel.Portability (restore a bundle
                              inline, import from YNAB/Monarch); Services/PortabilityDialogs (IPortabilityDialogs);
                              ImportWorkflow hands YNAB/Monarch files to MigrationWorkflow.
  Tests:                      Infrastructure Portability/ (PortabilityKit: every PRD 6.2 table + stored-value table
                              hashes; CsvExportTests; BundleRoundTripTests; BundleTimingTests in TimingCollection) and
                              Import/Migration/ (fixtures in Import/Fixtures/Migration with .expected.json); Desktop
                              PortabilityTests (FakePortabilityDialogs, MigrationSamples), PortabilityRenderingTests
                              (PortabilityScenes), new methods in AccessibilityTests and HighDpiRenderingTests.
M9c tags, attachments and payee merge (F-TXN-8, F-TXN-9; ADR 0096, 0097):
  Keel.Domain/Ledger/TagNames   Clean (trim, one leading '#', 100 chars), case-insensitive Same, reserved "Flagged".
                              SearchQuery gains has:tag / has:attachment; free words also match tag names.
  Keel.Application/           Tags/ITagService (ListAsync with counts, Rename, Merge, Delete; TagUsage, TagMergeResult);
                              Attachments/IAttachmentService (+AttachmentDto); IPayeeService.PreviewMergeAsync/MergeAsync;
                              SaveTransactionRequest.Tags (null keeps), TransactionDto.Tags, RegisterFilter.TagId,
                              RegisterRow.Tags/AttachmentCount; LedgerAction and LedgerError values for all three.
  Keel.Infrastructure/        Tags/TagService (GetOrAddAsync + SetTransactionTagsAsync: every tag write, rules too),
                              Attachments/ (AttachmentService: hash-named files in Name.keel-attachments, copy before the
                              row, orphan clean-up with a 30-day audit grace; AttachmentFiles), Ledger/PayeeService.Merge,
                              Rules/RuleRewriter (renames/merges rewrite rules' tag and payee names in the same action).
  Keel.Desktop/               Services/AttachmentFiles (IAttachmentFiles: picker + launcher; tests fake it), ViewModels/Register/
                              TagEditorViewModel + EditorAttachmentsViewModel (editor second line), TagOption filter,
                              Settings/TagsSettingsViewModel (+ rename/merge dialogs, Views/Settings/), Rules/PayeesViewModel
                              selection + MergePayeesDialogViewModel (Views/Rules/MergePayeesDialogView), Styles/Tags.axaml.
                              LedgerText looks up later features' texts under their prefix (Tag_Error_…, Attachment_Action_…).
  tests/                      Infrastructure Tags/, Attachments/, Categorization/PayeeMergeTests; Desktop TagsAttachmentsTests,
                              TagTestLedger + FakeAttachmentFiles, M9cRenderingTests, AccessibilityTests/HighDpiRenderingTests.Tags….
```

Dependency direction: `Desktop -> Application -> Domain`; `Infrastructure -> Application -> Domain`.
Desktop references Infrastructure only from `Program.cs` (DI registration). Tests may use
`Program.CreateHost` to get the real DI graph.

## Conventions

From PRD 15, plus decisions made while building M0.

**Process**
- Work milestone by milestone (PRD 12); within one, vertically: domain, application,
  infrastructure, UI, tests. Keep the solution building and tests green after every commit.
- Conventional commits (`feat(budget): ...`, `fix(import): ...`, `test(domain): ...`), small and
  frequent. Push at least at every milestone exit.
- Every deviation from the PRD, library substitution, or unlisted feature gets an ADR in
  `docs/decisions/NNNN-title.md` (see 0001). Record each milestone's commands and results in
  `CHANGELOG.md`.
- Tests first for PRD section 6: write the golden tests (6.4.7, 6.4.8) before `BudgetCalculator`.
- No placeholder UI: every screen in scope has designed empty, loading and error states; never a
  "TODO" view. Accessibility and keyboard are features: set `AutomationProperties.Name` on icon
  buttons and add shortcuts as controls are built.
- Cross-platform always: no Windows-only APIs outside `Keel.Infrastructure/Platform/Windows`
  (and likewise per OS). A red CI runner on any OS is a blocker.

**Domain and data**
- Money is `long` minor units; `Money` (amount + ISO currency) in the domain; never `double`.
  `decimal` only at UI/file boundaries (`Money.FromDecimal`, `Money.TryParse`, `Money.Format`).
  Outflows negative, inflows positive, for every account type. Percent splits: banker's rounding,
  remainder on the last part.
- `DateOnly` for ledger dates, stored as ISO `yyyy-MM-dd` TEXT (EF Core's SQLite default; a test
  asserts the stored text). No time component in the ledger.
- Timestamps are UTC `DateTime` named `...At`; a value converter marks them UTC on read.
- Ids are `Guid` v7 from `EntityIds.New()` (time-ordered, creatable before insert). System rows
  (default profile, Inflow and Credit Card Payments groups, Ready to Assign) use fixed ids in
  `SystemIds` and are seeded by the initial migration.
- Enums are stored by name (readable in the user's file, safe to reorder).
- `Transaction` is soft-deleted (`IsDeleted`); a global query filter hides deleted rows and their
  splits, tags and attachments. Use `IgnoreQueryFilters()` only for undo and audit.
- sum(splits) == parent amount is enforced by triggers plus a deferred constraint (ADR 0003).
  Tables rebuilt by a future migration must recreate those triggers.
- Every connection runs `journal_mode=WAL`, `foreign_keys=ON`, `synchronous=NORMAL`,
  `busy_timeout=5000` (`SqlitePragmaInterceptor`). Use `IDbContextFactory<KeelDbContext>` and
  one short-lived context per unit of work. No EF entities in view models.
- After any model change: add a migration and keep `Model_has_no_changes_missing_from_migrations`
  green. Budget files with unknown (newer) migrations are refused (`BudgetFileTooNewException`).
- App settings (theme, last file, window placement per display configuration) live in
  `<datadir>/settings.json`; data-file settings go in the `Setting` table. Data dir: Windows
  `%APPDATA%\Keel`, macOS `~/Library/Application Support/Keel`, Linux `$XDG_DATA_HOME/keel`
  (default `~/.local/share/keel`).
- Logs never contain payee names, amounts, or secrets. Use `[LoggerMessage]` source-generated logging.
- Every ledger mutation goes through `LedgerWriter.RunAsync(LedgerAction, ...)` and saves with
  `LedgerSession.SaveAsync` (never `SaveChangesAsync` directly): it writes `AuditEvent` before/after
  JSON per row, records the session undo entry (50 deep), and publishes `LedgerChanged` after the
  commit. Undo replays recorded row states, so mutate tracked entities (no raw SQL writes) and add
  new rows with a client key via `DbSet.Add`, not through a navigation collection.
- Services run on the thread pool (`Task.Run`); view models receive `LedgerChanged` off the UI
  thread and marshal with `Dispatcher.UIThread.Post`. An empty `AccountIds` set means "any account".
- The register never loads all rows: `IRegisterQuery` pages in SQL (200 rows), running balances
  are the ledger balance in (Date, Id) order (ADR 0009), and the grid binds to `RegisterSource`
  (ADR 0010). Guids are compared in raw SQL as upper-case text (`RunningBalance.SortKey`).
- Refused operations throw `LedgerValidationException(LedgerError)`; the UI maps codes to
  `LedgerError_*` strings through `LedgerText`.
- Budget math lives only in `BudgetCalculator` (PRD 6.4 literal; open points in ADR 0007). Services feed
  it from `BudgetAggregationQuery` and never compute budget numbers themselves. A changed golden file
  (`*.received.txt` next to the test) is reviewed by hand and then renamed to `*.verified.txt`; add a
  golden case for every budget-math bug fixed (PRD 13).

**UI**
- MVVM with CommunityToolkit.Mvvm (`[ObservableProperty]` partial properties, `[RelayCommand]`);
  no ReactiveUI. View models derive from `ViewModelBase`; screens from `PageViewModel`.
- View-model-first navigation: `INavigationService.NavigateTo<TViewModel>()`. The `ViewLocator`
  maps `Keel.Desktop.ViewModels.FooViewModel` to `Keel.Desktop.Views.FooView`; a test fails if a
  view model has no view. Register new screens in `Services/DependencyInjection.cs` and add a
  sidebar entry in `ShellViewModel` if they are top-level.
- Compiled bindings are on: every view declares `x:DataType`.
- All user-visible text goes in `Resources/Strings.resx` (strongly typed `Strings` class,
  `{x:Static res:Strings.Key}` in XAML).
- Colours, sizes and spacing come from `Styles/Tokens.axaml` (light and dark theme dictionaries,
  8-px grid, 13-px base font); use `DynamicResource` for brushes. Icons are 24x24 line geometries
  in `Styles/Icons.axaml` drawn with `controls:Icon` (ADR 0004).
- Shortcuts use the platform command modifier via `PlatformShortcuts` (Cmd on macOS, Ctrl
  elsewhere) and display with `PlatformShortcuts.Format` (glyphs on macOS).
- Dialogs are in-window (`DialogService.ShowAsync(DialogViewModel)` rendered by the shell's dialog
  layer through the view locator); the status strip (`StatusService`) carries the undo toast.
- Amount inputs use `controls:MoneyTextBox` bound to `long` minor units (inline `+ - * /` math).
- A form that saves on Enter from a `MoneyTextBox` handles `KeyDown` on the box (bubble) instead of a
  `KeyBinding`: key bindings run before the box evaluates its text, so they would save the old value.
- Budget screen keys (PRD 9.3, listed in Settings > Keyboard shortcuts from the `ShortcutRegistry`):
  arrows move the cell cursor; Enter/F2 edits Assigned (or opens Activity, moves money from
  Available, toggles a group); typing a digit starts editing; in the editor Enter saves and moves down,
  Tab/Shift+Tab save and edit the next/previous Assigned, Esc cancels; `M` move money, `T` target,
  `I` inspector, `Q` quick-assign palette, `Alt+←/→` months, `Ctrl/Cmd+Shift+F` fund targets.
- The budget view model keeps `BudgetLedgerData` (ADR 0041): recompute on `BudgetChanged`, reload on
  `LedgerChanged` (lazily while hidden), all through one pump on the UI thread; budget writes go
  through `BudgetViewModel.QueueWrite` so the undo order is the user's order.

**Import**
- Parsers are pure (bytes + `ImportOptions` in, `ParseResult` out) and never throw on bad rows:
  they add an `ImportWarning` with a code and line, never payee or amount text.
- Every importer change needs a fixture: add the anonymized file to
  `tests/Keel.Infrastructure.Tests/Import/Fixtures/` (bytes are kept exactly by the folder's
  `.gitattributes`) and its reviewed `.expected.json`.
- `PayeeNoiseTable` feeds stored fingerprints: bump `PayeeNormalizer.Version` when it changes
  (ADR 0005). Every table entry has a test.
- Every import source calls `IImportService.ImportTransactionsAsync(source, batch)`; never insert
  imported rows another way. Dedup, payee resolution, hooks and transfer detection live in
  `ImportPlanner`, which the preview and the import share, so a preview always matches its import.
- Rules and the learner plug in as an `IImportCategorizationHook` registered in DI (all hooks run in
  order; the no-op default stays). Hooks must not write; they also run during previews.
- New imported rows are written by `BulkLedgerInsert` (ADR 0052): the one allowed raw-SQL ledger
  write, because it records the same row snapshots and audit rows as `LedgerSession.SaveAsync`.
  Columns and converters come from the EF model; add nothing hand-written there.
- Import memory (CSV mapping per account with its header row, last folder) is in the `Setting`
  table through `IImportSettingsStore`, not audited or undone (ADR 0051).
- Desktop: the import flow is `ImportWorkflow`; tests swap `ImportWorkflow.FilePicker` for a fake.
  Wide dialogs override `DialogViewModel.PreferredMaxWidth`.

**Reports, goals and dashboard (M6)**
- Report numbers come only from `IReportService` (raw SQL `GROUP BY`, never row loads); the counting
  rules (system rows, transfers and tracking toggles, uncategorized rows, previous period, net-worth
  snapshot rule, goal pace) are in ADR 0060. With default toggles Spending equals the negated budget
  Activity; keep the cross-check test green.
- Charts use LiveCharts through `ReportChartKit` (animations off, built-in legend hidden) and colours
  from `Styles/Charts.axaml` via `ChartPalette` slots: eight slots in fixed order, then "Other".
  Every chart has a legend or table with names and values (colour is never the only signal) and every
  chart element and table row drills down (usually to the register through `RegisterNavigation`).
- Headless captures must wait for LiveCharts' throttled redraw (a few dispatcher cycles with short delays).
- Upcoming bills and the forecast low point on Home come from the recurring, scheduling and forecast
  services (M5); no dashboard card is a placeholder any more.
**Recurring, scheduling and forecast (M5)**
- Recurrence rules are stored in canonical form (`RecurrenceRule.Parse(text).ToString()`); the
  schedule's start date is the rule's DTSTART. Days of month clamp to short months (ADR 0030).
- Detection, reconciliation, alerts and the forecast are pure domain code; services load inputs,
  apply the returned decisions and store alerts. Group by `PayeeNormalizer` output, never a second
  normalizer. New detections are `RecurringStatus.Detected`; only `Active` items are forecast.
- Alerts are idempotent by the `key` in `Alert.PayloadJson` (`AlertKeys.Of`); pass the keys of
  every stored alert, dismissed ones included, to `AlertEvaluator.Evaluate`.
- A changed recurring or forecast golden (`*.received.txt`) is reviewed by hand, then renamed to
  `*.verified.txt`.
- Services (ADR 0035): scheduled rules are stored explicit, without COUNT/UNTIL (that is `EndDate`), and
  evaluated from `NextDate`; only a schedule's next instance is entered or skipped, through
  `TransactionService.SaveCoreAsync` inside the same `LedgerWriter` unit. Automatic detection runs use
  `LedgerWriter.RunCoreAsync(..., Recording.None, ...)` (audited, not undoable); user item actions are
  undoable. Alerts are written directly and publish `AlertsChanged`. Data-file settings for M5 live in the
  `Setting` table through `DataFileSettings` (keys `recurring.*`, `forecast.settings`).
- `RecurringJobs` (started by the shell) enters due schedules and prompts on start and day change, runs
  detection once per app day and after imports (audit-log watermark), and invalidates the forecast on
  `LedgerChanged`/`RecurringChanged`. Tests await `shell.Jobs.Running` before asserting.
- Detail panels bound to a nullable view model use a `ContentControl` with a typed `DataTemplate`, so
  `$parent[...]` bindings never run against a null `DataContext` (the rendering tests fail on binding warnings).
**Rules and learner**
- Rules are pure: `RuleEngine.Compile(rules).Apply(snapshot)`; persist only `RuleJson` output and read with
  `RuleDefinition.FromEntity` (newer formats throw `RuleFormatException`). New condition/action kinds get a new
  `type` discriminator; changing a kind's meaning needs a format version bump.
- The learner never writes below `CategoryLearner.MinimumConfidence` (0.60) and needs 3 examples of a payee or word.
  `LearnerModel` holds integer counts only, so its JSON is identical on every OS; keep it that way. Accuracy
  thresholds are asserted in `LearnerAccuracyTests` on the deterministic fixture; rerun them after any change to
  features, smoothing or `PayeeNormalizer`.
**Review and rules UI (M4)**
- Every categorization write goes through `ICategorizationService`/`IRuleService` (one `LedgerWriter` action
  each); the learner follows automatically by replaying the audit log (ADR 0027), so never call it after a
  write. Inside a ledger write (import hooks) use `ILearnerService.PeekModelAsync`, which never writes.
- Retroactive apply and review approvals with rules write through `MutationPlanner.Plan` + `WriteAsync`; a
  preview and its apply share the plan, so keep new rule effects in the planner (ADR 0026). The rule
  "flag" is the reserved tag `Flagged`.
- Review keys (ADR 0028): every decision approves and advances; `J/K` only move. Row templates bind to
  commands through an owner property on the row view model (`Owner`, `Editor`, `Choose`), not
  `$parent[...]`, which logs binding errors while a view is torn down.
**Data file, sessions and packaging (M8)**
- A session is one host over one open file (ADR 0080). Anything holding file data must be a session
  service (never a static); timers implement `IDisposable`. Switch files only through `BudgetSessions.OpenAsync`.
  Tests: `TestHost.Current<T>()` resolves from the running session, `TestHost.CreateFirstRun()` shows the setup,
  and every other `TestHost` starts with `firstRunCompleted: true`.
- Backups, restore and moves copy the database with the SQLite backup API (`BackupArchive`), never a file copy of
  a live database; every copy is verified by reopening it (ADR 0081). The pragma interceptor must keep skipping
  `journal_mode` on read-only connections.
- Every shortcut is registered once in `ShortcutRegistry` (tests compare it with the window bindings and with
  `docs/user-guide/keyboard-shortcuts.md`); every user action that is not row-level goes into `AppCommands`, which
  feeds the palette and the menus. Page-level single keys go through `PageKeys` (ADR 0085).
- Packaging: `build/package.sh` / `package.ps1` with the `vpk` local tool pinned to the Velopack package version;
  the version comes from `Directory.Build.props`; never create tags from an agent session.
- The update check stays off by default and creates no update source while off (PRD 10).
**Accessibility (M8)**
- `AccessibilityTests` audit names, contrast and tabular figures over live screens; add new screens and dialogs to it.
  New colour tokens need a `TokenContrastTests` pair. Use Keel tokens for text on accent (`AccentButtonForeground`,
  `Keel.SelectionForeground`) rather than Fluent defaults.
- Layouts must work at 960 × 540 logical pixels (1080p at 200%); `HighDpiRenderingTests` renders every screen there.
**Bank sync (M7)**
- Secrets only through `ISecretStore` under `SecretKeys` names; never in the database, `settings.json`, logs or
  test output. The UI learns only whether a value is set (`IBankCredentialsService`) and shows dots plus Replace.
- Providers implement `IBankDataProvider` and throw `BankProviderException(status, code)`; the sync service turns
  failures into connection health (`NeedsReauth`, `Error`), never dialogs. Log codes only, never tokens or payees.
- Synced rows enter only through `IImportService` (source Provider, `SyncConnectionId` set); the cursor is saved
  after the page's writes commit. Connection state (cursor, health, last sync) is saved directly, not audited.
- Plaid HTTP goes through `IPlaidApi` and the `PlaidClient` named client (resilience); tests swap the primary
  handler for `FakePlaidServer`. Desktop tests replace the providers with `FakeBankProvider` through
  `TestHost.Create(configure)`, and every desktop test host uses `InMemorySecretStore`.
- `Tmds.DBus.Protocol` stays on the version Avalonia.FreeDesktop uses (ADR 0070).
