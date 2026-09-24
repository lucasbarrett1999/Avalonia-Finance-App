# 6. The register grid uses a custom paged collection view and an inline editor row

- Status: Accepted
- Date: 2026-09-24
- Milestone: M1

## Context

PRD 7.3 asks for registers on `DataGrid` "with ItemsSourceView over a paged, DB-backed source;
never bind 100k rows to an ObservableCollection", and F-ACC-2 asks for inline editing of every
field. Avalonia's `DataGrid` (11.3.13) wraps any `ItemsSource` that is not an
`IDataGridCollectionView` in its own `DataGridCollectionView`, which copies the whole source into
an internal list on construction. A plain virtual `IList` would therefore be enumerated end to end
(100k page fetches) as soon as it is bound. The grid's own cell editing is built around that
collection view and edits one cell at a time.

## Decision

- `Keel.Desktop.ViewModels.Register.RegisterSource` implements `IList` and
  `IDataGridCollectionView` itself. It reports the total count from the database, materializes
  placeholder rows only for pages the grid asks for (200 rows per page, at most 30 cached),
  fills them in place when the page arrives, and forwards header-click sorting to the database
  query through its `SortDescriptions`.
- The grid is read-only. Adding and editing happen in an inline editor row above the grid (PRD
  9.4: "Add/edit row appears inline at the top"), whose columns are kept aligned with the grid's
  actual column widths. Enter on a row opens it there; Enter saves, Ctrl/Cmd+Enter saves and
  starts another, Esc cancels.
- Refreshes after `LedgerChanged` update cached pages in place when the row count is unchanged,
  so selection and scroll position survive; otherwise the source resets and the edited row is
  re-selected by its index from the database.

## Consequences

- Opening a 100k-row register touches one page; headless tests assert that at most two pages are
  materialized and fewer than 80 row containers are realized.
- Grouping and client-side filtering of the grid are not supported (filters run in SQL).
- If a future Avalonia DataGrid stops copying its source, the custom view can be dropped.
