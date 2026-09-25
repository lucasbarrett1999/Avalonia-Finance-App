# 90. The three-month budget view

- Status: Accepted
- Date: 2026-09-25
- Milestone: M9a (P1 backlog: F-BUD-2 three-month mode, PRD 9.3)

## Context

F-BUD-2 (P1) asks for "a 3-month side-by-side mode" of the budget grid, and PRD 9.3 lists it as a P1
toggle. The grid is the purpose-built hierarchical grid of ADR 0040 with one cell cursor, one Assigned
editor and the PRD 9.3 keyboard map, over the loaded ledger data of ADR 0041 (12 months back, 3 ahead,
recomputed on `BudgetChanged`, reloaded on `LedgerChanged`). The screen must stay usable at 960 × 540
logical pixels (PRD 11, `HighDpiRenderingTests`).

## Decision

- **One active month.** In three-month mode `BudgetViewModel.CurrentMonth` is the month the cell
  cursor is in and `MonthOffset` (0–2) its position in the visible window; `WindowStart` =
  `CurrentMonth − MonthOffset`. Every existing command (assign, Tab/Enter, quick assign, move money,
  fund targets, targets, Activity, the inspector, month notes) keeps acting on `CurrentMonth`, so none of
  them changed. The row view models keep the active month's numbers; each row also has three
  `BudgetMonthCellViewModel`s for the window, and the month headers (`BudgetMonthHeaderViewModel`) show
  each month's Ready to Assign (selecting one explains it).
- **Cursor.** `←/→` walk Name, then Assigned/Activity/Available of each month left to right; crossing
  into another month moves `CurrentMonth` in memory. Clicks on a month's cell select that month first.
  `Alt+←/→` shift the window by one month and keep the cursor's column in the window; the month picker
  and Today start the window at the chosen month.
- **No reload.** The window needs its three months inside the loaded range; the range (12 back, 3
  ahead) already covers a window starting at the shown month, so turning the mode on, shifting the window
  or moving the cursor between months recomputes nothing and reloads nothing (a test asserts the same
  `BudgetLedgerData` instance). A month outside the range reloads as before.
- **Templates.** The grid's `ItemsControl` uses `BudgetRowTemplateSelector` (one-month and three-month
  templates for group and category rows); switching modes re-adds the rows so every container is
  rebuilt with the right template. Only the active month's cell shows the Assigned editor; the editor
  binds to the row, so the existing key handling and tests are unchanged.
- **Width.** A month is 288 px (Assigned 92, Activity 92, Available 104, compact cell padding) and the
  Name column shrinks to 160 px, so the table needs 1,036 px. Narrower (a small window, the inspector
  open, or 960 × 540) the table scrolls sideways inside the grid border, and moving the cursor brings its
  cell into view. We chose scrolling over narrower columns (amounts like "($12,500.00)" would clip) and
  over floating the inspector over the grid (it would hide the third month). At view widths under 760 px
  the Fund targets and Move money buttons show their icons only, because the two new toggles need the
  room next to the Ready to Assign pill; names and tooltips are unchanged.
- **Persistence.** The mode is `AppSettings.BudgetThreeMonths` in `settings.json` (a view preference,
  like the theme), restored when the screen is created. Shortcut `W` ("wide"): digits start typing an
  amount on the grid and `M`, `T`, `I`, `Q` are taken.

## Consequences

- A window shift re-lays out three times the cells of a one-month switch: over the 100k-transaction
  fixture the view model work stays under 20 ms but layout makes the headless median about 110–130 ms in
  the sandbox (one month: about 40 ms). PRD 11's 100 ms target is kept for the one-month view; the
  three-month test allows 250 ms.
- Drag and drop of an Available pill moves money in the active month (the pill's month becomes active
  when it is pressed).
- The Manage categories dialog now sizes its list up to 440 px instead of exactly 440 px, so it fits
  at 960 × 540.
