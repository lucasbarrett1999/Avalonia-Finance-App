# 91. Flex mode: tags, defaults and the one-number view

- Status: Accepted
- Date: 2026-09-25
- Milestone: M9a (P1 backlog: F-BUD-6, PRD 9.3)

## Context

F-BUD-6: categories are tagged Fixed, Non-monthly or Flex; the Flex view shows total income, total
fixed, total non-monthly set-aside and one Flex number with spending progress; assignments stay
underneath and there is no separate data model. `Category.FlexKind` (Unset/Fixed/NonMonthly/Flex) has
existed since the initial migration. Section 6.4 is normative, and budget math lives only in
`BudgetCalculator`.

## Decision

- **No schema change.** Tags are `Category.FlexKind`, stored by name; `Unset` means automatic.
  `ICategoryService.SetFlexKindAsync` writes one tag as one audited, undoable `LedgerAction.TagCategoryFlex`
  through `LedgerWriter` (system and card payment categories are refused like other protected edits).
  It publishes `LedgerChanged`, so the Budget screen reloads its category data.
- **Defaults, never overriding.** `FlexClassifier.Effective(tag, target)`: a user's tag always wins;
  an untagged category with a target that asks for the same amount every month (monthly set-aside,
  monthly spending, debt payment) is Fixed, one with a savings-balance-by-date target is Non-monthly,
  and one without a target is Flex. The default is computed when the month is shown, not written, so
  it follows target changes and a user's choice is never replaced.
- **Pure aggregation over the grid's result.** `FlexSummary.Compute(BudgetMonthResult, kinds)` in
  `Keel.Domain/Budgeting` sums the calculator's own cells: income is `InflowRTA(M)`
  (`BudgetMonthResult.InflowThisMonth`, the Inflow group's activity); each bucket sums Carry, Assigned,
  Activity and Available of its categories. `BudgetDtoMapper` computes it from the same
  `BudgetMonthResult` it maps for the grid and puts it on `BudgetMonthDto.Flex`, and each category DTO
  carries its tag and effective kind. Golden and property tests check that the buckets are sums of grid
  cells and add up to the visible group rows.
- **What counts.** Only visible regular categories (the group-row rule of 6.4.3). Credit Card Payment
  categories belong to no bucket (their activity is spending already counted in the spending
  categories, 6.4.5); the view says so.
- **The numbers.** Fixed shows Σ Assigned (and what was spent); Non-monthly shows Σ Assigned as the
  month's set-aside (and Σ Available as saved so far). The Flex number is Carry + Assigned of the Flex
  categories, so that Flex Available = Flex number − spent when all activity is spending; the bar is
  spent (−Activity, floored at 0) over the Flex number, red when Flex Available is negative, with a
  marker at the share of the month before today. `FlexSummary.Pace(today)` counts days left including
  today (all days for a future month, none for a past one) and safe-to-spend per day =
  max(0, Flex Available) / days left, rounded down to the minor unit.
- **Drill-down.** A Flex-view number opens the grid filtered to its categories (a banner with "Show all
  categories" clears the filter); income opens the register filtered to Ready to Assign and the month.
- **Tagging UI.** The inspector has a "Flex view group" picker (Automatic, Fixed, Non-monthly, Flex)
  with a hint naming the effective kind; Manage categories has the same picker per user category.
- **Persistence and keys.** The view choice is `AppSettings.BudgetFlexView`; `F` toggles it (Fund targets
  stays `Ctrl/Cmd+Shift+F`). The Flex view always shows one month; turning it on leaves three-month mode.

## Consequences

- The Flex view adds no budget math: a change to 6.4 changes the Flex view through the same result.
- Hidden categories are not in the Flex view, as they are not in group rows; their money still counts
  in Ready to Assign.
- The summary is computed for every loaded month (a few dictionary sums per month), which is
  negligible next to the calculator.
