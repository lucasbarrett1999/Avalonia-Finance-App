# 40. The budget grid is a purpose-built hierarchical grid, not TreeDataGrid

- Status: Accepted
- Date: 2026-09-24
- Milestone: M2 (Budget screen)

## Context

PRD 7.1 pins `Avalonia.Controls.TreeDataGrid` 11.3.2 for the hierarchical budget grid, and PRD
9.3 asks for collapsible group rows with totals, category rows, an editable Assigned column where
Tab moves down the column and Enter commits and moves down, arrow navigation between cells, a
clickable Activity cell, and an Available pill that can be dragged onto another row.

Since 11.2.0, TreeDataGrid requires a commercial Avalonia Accelerate licence. Referencing 11.3.2
fails the build without a licence key:

```
error AVLIC0001: No valid AvaloniaUI license keys found for required commercial products:
"Avalonia.Controls.TreeDataGrid".
```

A licence key cannot be committed to an open repository or required from every contributor and
CI runner, and PRD 1.1 (no dark patterns, user-owned software) argues against a paid dependency.
11.1.1 is the last MIT release: it builds against Avalonia 11.3, but it is unmaintained, pulls in
System.Reactive, and the fixes listed for 11.3.x are exactly the editing and keyboard-focus bugs
this grid depends on (edit controls not focused on entering edit mode, focus not following
keyboard cell selection).

## Decision

- Do not reference TreeDataGrid. The budget grid (`Views/BudgetView.axaml`) is an `ItemsControl`
  over a flattened row list (`BudgetViewModel.Rows`): group rows followed by the category rows of
  expanded groups, both with the same four fixed-width columns (Name, Assigned, Activity,
  Available). Collapsing a group removes its rows from the list.
- The view model owns the cell cursor (row and column), edit mode and the keyboard map; the view
  translates keys (tunnel handler), clicks and drag-and-drop into view-model calls. Only the
  edited row shows a `MoneyTextBox`; every other Assigned cell is text.
- No virtualization: a budget has tens to a few hundred rows (the 100k-transaction fixture has 48),
  and all rows are updated in place on a month switch (about 30–50 ms including layout at 100k
  transactions in the sandbox).
- The package version stays listed in `Directory.Packages.props` (unused) so the PRD pin is visible;
  removing or licensing it is the owner's call.

## Consequences

- Keyboard behaviour is exactly the PRD's and is covered by headless tests; no licence is needed.
- Column resizing, sorting and virtualization are not available in the budget grid; none is
  required by PRD 9.3. If a budget ever needs thousands of rows, switch the items panel to a
  virtualizing panel (the view model already exposes a flat list).
