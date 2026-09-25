# 103. Command palette completeness and enablement

- Status: Accepted
- Date: 2026-09-25
- Milestone: M9 (stream E)

## Context

F-SET-5 asks for a palette "listing every action". ADR 0085 put every page, Settings section and the main
screen actions into `AppCommands`, but many screen actions were still buttons only (Bills months and tabs,
budget months, the inspector, quick assign, reconcile, record balance, rules, notifications, updates, restore
from a file, encryption), every command was always runnable even where it could not work, and nothing stopped
a new screen action from being added without a palette entry.

## Decision

- **Every non-row action is a palette command.** A new block at the end of `AppCommands.Build` adds the
  missing actions of every screen (M1–M9): reports and Bills tabs as "Go to" entries showing their page keys,
  file actions (restore from a file, encrypt, remove encryption, unlock), view settings (closed accounts,
  notifications, accent colours, motion), register actions for the register on screen (reconcile, edit
  account, record balance, clear filters, sync, reconnect, new scheduled transaction), budget (previous/next/
  this month, inspector, quick assign, explain Ready to Assign, manage categories), review batch approval,
  rules (new, apply all), Bills months, report refresh and all accounts, the Home checklist, notifications,
  add connection, and updates. Page actions navigate to their page first, then run.
- **Row-level stays out.** Actions on one selected or focused item (toggle cleared, approve, delete, the Review
  decisions, a bill's detail actions, the cell under the budget cursor) and editor/dialog/form keys remain keys
  and buttons only, as ADR 0085 said.
- **Enablement.** `AppCommand.IsEnabled` (an init property, so the record keeps its shape) is evaluated when the
  palette opens, from the open file (none, locked, plain or encrypted) and the current page only; building the
  list never creates a page that is not shown. The palette lists unavailable commands with "Unavailable here"
  (and "unavailable" in the screen-reader name) so they can be found, preselects the first available result,
  and refuses to run an unavailable one with a message instead of closing. Menus are built once per session and
  keep guarding in the action itself.
- **The check that keeps it complete.** `CommandPaletteTests` reflects over every page view model, every Settings
  section view model and the shell, notification centre, connections, payees and register schedule panels, and
  requires each parameterless `[RelayCommand]` either to map to an existing palette command or to be listed as
  row/editor/form-level with a reason; it also requires every `ShortcutRegistry` entry to have a palette command
  with its id (or numbered ids for "1–4"-style keys, or a listed alias) unless it is listed as row-level. Stale
  entries fail too. A new screen action therefore fails the test until it is added to the palette or classified.

## Consequences

- The palette grows from about 50 to about 100 commands (plus one per account); fuzzy ranking keeps the page
  targets first.
- Parameterized commands (`IRelayCommand<T>`) are treated as row-level by construction; a screen action that
  takes a parameter needs a parameterless palette entry of its own.
