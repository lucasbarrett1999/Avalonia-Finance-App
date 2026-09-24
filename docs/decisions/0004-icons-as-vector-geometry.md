# 4. Icons are vector geometries, not SVG files

- Status: Accepted
- Date: 2026-09-24
- Milestone: M0

## Context

PRD 7.1 lists `Avalonia.Svg.Skia` 11.3.0 for icons, and PRD 8 requires all assets to be vector
("SVG via Avalonia.Svg.Skia") or multi-resolution. The M0 shell needs about twenty
single-colour line icons that must follow the theme's foreground colour (light, dark, selected,
disabled) and scale crisply at any DPI.

## Decision

Icons are `StreamGeometry` path data on a 24x24 grid in `src/Keel.Desktop/Styles/Icons.axaml`,
drawn by the small `Keel.Desktop.Controls.Icon` control with a 2-px round stroke in the
inherited `Foreground`. `Avalonia.Svg.Skia` stays pinned in `Directory.Packages.props` but is
not referenced yet.

## Consequences

- Icons are still pure vector, so the HiDPI requirement of PRD 8 is met.
- Recolouring for theme and state is free (it is just `Foreground`); SVG files would need
  per-theme copies or CSS overrides.
- One fewer native-rendering dependency in the shell.
- If multi-colour artwork (illustrations, institution logos) is needed later, reference
  `Avalonia.Svg.Skia` at the pinned version for those assets; line icons stay geometries.
