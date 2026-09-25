# 95. PNG export of report charts

- Status: Accepted
- Date: 2026-09-25
- Milestone: M9 (stream B)

## Context

PRD 9.8 gives every report toolbar "export CSV/PNG"; M6 shipped CSV only (ADR 0060).

## Decision

- The report toolbar has **Export PNG** next to Export CSV (`Ctrl/Cmd+Shift+E`, also in the command
  palette). It asks for a file through `IFileDialogs.SavePngAsync` (the platform save picker; tests use a
  fake), suggesting the CSV name with `.png`.
- The view renders **the selected report's chart control** (the first visible LiveCharts chart in the
  report host; for Spending the donut of the current drill level) with Avalonia's
  `RenderTargetBitmap` at **2x** (192 DPI, pixel size = 2 × the chart's size), then composes it onto
  the theme's chart surface colour so the image is not transparent. LiveCharts draws through Skia into
  the bitmap, so the image matches the screen (theme, palette, current data).
- Only the chart is exported; its legend and table are text on screen and are in the CSV. A report
  with nothing to show says so in the status strip instead of opening the picker.

## Consequences

- No new package: rendering uses Avalonia and LiveCharts as the screen does, on every OS; a headless
  test writes each report's PNG and checks the signature and the 2x size.
- Composition passes the source rectangle in bitmap pixels (`DrawImage(source, sourceRect, destRect)`):
  passing it in DIPs crops a 2x bitmap to its top-left quarter.
