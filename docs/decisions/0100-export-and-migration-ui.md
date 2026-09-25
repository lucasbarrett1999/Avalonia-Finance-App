# 100. Where export, bundle import and the YNAB/Monarch importers live in the UI

- Status: Accepted
- Date: 2026-09-25
- Milestone: M9 (stream D)

## Context

F-REP-6 puts Export and bundle import under Settings → Data file; PRD 9.10 step 1 lists "Import from
YNAB/Monarch export" on the first-run Welcome step, and the task adds "Restore from a Keel export bundle"
to Open existing. The first-run layer covers the whole window, including the dialog layer (ADR 0084), and
five M9 streams change Settings, the first run and the import flow at the same time.

## Decision

- **Settings → General** gains an "Export and import" block (`PortabilitySettingsViewModel`, hosted by the
  data-file section through one optional constructor parameter): **Export…** (dialog: CSV zip, CSV folder
  or Keel bundle, then the save or folder picker; a folder export goes into a new "Name export yyyy-MM-dd"
  folder inside the chosen one), **Import bundle into a new file…** (bundle picker, then a dialog with the
  bundle's source, date and counts and the new file's name and folder; confirming imports and opens the
  file in a new session, ADR 0080), **Import from YNAB or Monarch…** (`MigrationWorkflow`). The three are
  also palette commands (`export-data`, `import-bundle`, `import-ynab-monarch`); they have no shortcut
  and are not in the menus.
- **Pickers**: a separate `IPortabilityDialogs` (folder, zip, bundle save, bundle open) rather than new
  members on `IFileDialogs`, so the existing fakes and other streams' changes stay independent.
- **First run** (`FirstRunViewModel.Portability.cs`): the Open existing card gets **Restore from a Keel
  export bundle…**; because dialogs would be hidden under the first-run layer, the chosen bundle's details
  and **Restore and open** appear inline, restoring into the path of the name box above (prefilled
  "Source (restored)"). A new card **Import from YNAB or Monarch export…** picks the file first (a cancelled
  pick creates nothing), checks that it is such an export, creates the named file in a new session (the
  setup ends: the export brings accounts and categories) and shows the migration preview there; after an
  import it lands on Budget.
- **Register "Import file"**: when the chosen file parses as a YNAB or Monarch export, the migration
  preview takes over (the export has several accounts), so users do not need to find the Settings entry.
- **Migration preview**: one row per source account with a target (new account with the source name and
  a type among the on-budget types, an existing account, or Don't import), categories and tags to create,
  parse warnings, and a result line from the rolled-back dry run that is refreshed after every change
  (latest request wins). A YNAB budget export gets its own small dialog with the dry run's counts.

## Consequences

- `HighDpiRenderingTests` renders the new dialogs at 192 DPI and 960 × 540 without binding errors, but saves
  the 1x frame for review: a 2x `RenderTargetBitmap` of the dialog layer scales dialog content twice in the
  headless renderer (existing dialogs such as About behave the same), so those bitmaps are not a faithful
  picture.
- The first-run restore uses the file name box of the Create card; the inline panel repeats the target path.
