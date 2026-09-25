# 82. Appearance, formats and reduced motion

- Status: Accepted
- Date: 2026-09-25
- Milestone: M8

## Context

F-SET-2 asks for an accent colour, density (comfortable/compact) and number/date/currency formats that
follow the OS locale with an override; PRD 9.11 asks for WCAG AA text in both themes, tabular
numerals and motion that respects the OS "reduce motion" setting.

## Decision

- **Accent** is one of six named accents (teal default, blue, violet, green, orange, rose), each with
  a dark variant for the light theme and a light variant for the dark theme, chosen so the accent, the
  badge text on it and the text of accent buttons meet 4.5:1 (asserted by `TokenContrastTests`). A free
  colour picker was not added: arbitrary colours cannot guarantee contrast. `AppearanceService` sets
  the Fluent palette accent and Keel's accent tokens in both theme dictionaries.
- **Density** is a `compact` class on the main window; `Styles/Density.axaml` tightens padding and
  minimum heights on the 4-px half-grid.
- **Formats.** The override is a culture name in settings.json (`formatCulture`, null = follow the OS).
  It is applied process-wide at startup (`CultureInfo.DefaultThreadCurrentCulture`); changing it
  reopens the file in a new session so every screen formats again. UI text stays English (PRD 11).
- **Reduced motion.** An explicit choice in Settings wins; "Follow system" reads GNOME's
  `enable-animations` (Linux) and `com.apple.universalaccess reduceMotion` (macOS) once per process.
  Avalonia exposes no cross-platform API for it and Windows would need platform code, so on Windows
  "Follow system" means full motion and the Settings choice is the switch. The `reduceMotion` class
  removes transitions everywhere and stops the syncing spinner (its icon and text stay).
- **Fluent overrides for AA**: accent-button text (white on the light accent, near-black on the dark
  accent), placeholder text in the secondary text colour at full opacity in every state, and selected
  list items on the navigation selection colour. Amounts use tabular figures (`+tnum`) everywhere,
  including hero numbers and `MoneyTextBox`; Inter is the UI font (`WithInterFont`).

## Consequences

- New tokens need entries in `TokenContrastTests`; new screens are covered by `AccessibilityTests`.
