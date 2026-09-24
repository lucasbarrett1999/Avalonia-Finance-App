# CLAUDE.md

Guidance for Claude Code (and other agents) working in this repository. Keep it current at
every milestone (PRD 15.10).

Keel is a local-first, cross-platform (Windows, macOS, Linux) personal-finance desktop app:
envelope budgeting plus net worth, recurring bills and forecasts, on one user-owned SQLite
file. The spec is `docs/PRD.md`; read it before changing behaviour. Section 6 (domain model
and budget math) is normative. The build order is PRD section 12; M0 is done (see
`CHANGELOG.md`).

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
```

The app applies pending migrations itself when it opens a budget file; there is no
`database update` step.

## Solution map

```
Keel.sln, Directory.Build.props (net10.0, nullable, analyzers, version),
Directory.Packages.props (central package versions, pinned by PRD 7.1), global.json (SDK 10.0)
src/
  Keel.Domain/          Pure domain, no packages. Money + Currency, entities for every PRD 6.2
                        table (Entities/), enums, AccountTypeInfo (PRD 6.3), SystemIds (seeded rows).
                        Ledger/ (M1): TransferRules, SplitRules, ReconciliationMath, RunningBalance (ledger
                        order reference), PayeeNames, SearchQuery (F-TXN-7 syntax), MoneyExpression.
  Keel.Application/     Use-case interfaces and DTO records: IAccountService, IBudgetService,
                        IImportService, Sync/IBankDataProvider (PRD 7.5), Security/ISecretStore (6.7),
                        IBackupService, INavigationService, IDataDirectory, IBudgetFileService,
                        IAppSettingsStore/AppSettings, Messaging (IMessageBus, LedgerChanged, BudgetChanged).
                        M1: Ledger/ (ITransactionService, IRegisterQuery + DTOs, LedgerValidationException),
                        Payees/, Categories/, Accounts/IBalanceSnapshotService, Undo/IUndoService + LedgerAction.
  Keel.Infrastructure/  Persistence/ (KeelDbContext, entity configurations, SQLite pragma interceptor,
                        KeelDbContextFactory, design-time factory, Migrations/), Files/ (DataDirectory,
                        BudgetFileService), Settings/ (JsonAppSettingsStore), Logging/ (Serilog),
                        DependencyInjection.AddKeelInfrastructure.
                        Ledger/ (M1): LedgerWriter (unit of work: audit + undo + LedgerChanged), LedgerSession,
                        EntityChange, UndoHistory/UndoService, AccountService, TransactionService, PayeeService,
                        CategoryService, BalanceSnapshotService, RegisterQuery (raw SQL, paged).
                        Fixtures/LedgerFixtureGenerator (deterministic 100k-transaction ledger).
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
tests/
  Keel.Domain.Tests/          xUnit + Shouldly: Money, classification, entities.
  Keel.Infrastructure.Tests/  Real SQLite files in temp dirs: migrations, round trips, pragmas,
                              indexes, soft delete, split-sum constraint, data dir, settings, logging.
                              M1: Ledger/ service tests (LedgerTestHost = real DI over a temp file),
                              register paging/running balance/search, undo, fixture + 100k timing.
  Keel.Desktop.Tests/         Avalonia.Headless.XUnit with Skia: shell smoke tests, navigation,
                              theme, shortcuts, window state, rendering in light and dark.
                              M1: RegisterTests (keyboard add, inline edit, C, delete + undo, reconcile,
                              100k virtualization), MoneyTextBoxTests, fixture rendering.
  Keel.Benchmarks/            BenchmarkDotNet (Money baseline; register/calculator/import to come).
                              M1: RegisterBenchmarks over the 100k fixture.
docs/  PRD.md, competitive-analysis.md, build-environment.md, decisions/ (ADRs)
.github/workflows/ci.yml  Build+test on windows/macos/ubuntu, format check, vulnerable-package scan.
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
