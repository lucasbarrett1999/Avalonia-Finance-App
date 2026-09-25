# 86. Accessibility audits and narrow layouts

- Status: Accepted
- Date: 2026-09-25
- Milestone: M8

## Context

PRD 8, 9.11 and 11 ask for screen-reader names on all controls, WCAG AA text contrast in both themes,
200% scaling, full keyboard operation and designed empty, loading and error states. Checking these by
eye per screen does not stay true as screens change.

## Decision

- **Audits run on the live visual tree** (`AccessibilityAudit` in the desktop tests) for every
  screen with fixture data, every report and Bills tab, the main dialogs, the palette, the
  notification panel and each first-run step, in light and dark:
  - every interactive control a user reaches (buttons, toggles, text boxes, lists, combo boxes,
    menus, pickers; not parts of another control's template) has a non-empty automation name from
    Avalonia's automation peer;
  - every visible, enabled text block has at least 4.5:1 contrast (3:1 at 24 px, or 18.66 px bold)
    with the first opaque background behind it, translucent layers composed; gradients, images and
    chart canvases are skipped (charts carry tables and legends with text);
  - money text uses tabular figures.
- **Token contrast** is also asserted pairwise for the whole palette and every accent
  (`TokenContrastTests`): body text on every surface, semantic colours on page and card backgrounds,
  foreground tokens on their own backgrounds (4.5:1), meaningful graphics such as the budget cursor,
  bars, rings and today's border (3:1).
- **200% scaling** is checked by rendering every screen at 192 DPI in a 960 × 540 logical window,
  the space a 1080p display offers at 200%. The window's minimum height is lowered from 560 to 520 so
  it fits. Budget adapts below 1120 px (the Ready to Assign pill and the actions move under the month)
  and below 980 px (the inspector floats over the grid instead of squeezing it); Home's checklist
  titles wrap and the spending total shrinks instead of clipping.
- **Loading states** were added to Home and Goals, the two screens that showed nothing before their
  first load.
- **Focus order** follows reading order; the first-run account step is tested with Tab.

## Consequences

- A new screen or dialog should be added to `AccessibilityTests`; a failing audit names the control
  and its place in the tree.
- Contrast of chart series colours against each other is covered by ADR 0060 (legends and tables
  carry every value), not by these audits.
