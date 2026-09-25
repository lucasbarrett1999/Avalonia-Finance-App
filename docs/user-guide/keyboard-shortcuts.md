# Keyboard shortcuts

Keel can be used without a mouse. **Settings → Keyboard shortcuts** lists every shortcut with the
keys for your system; on macOS, `⌘` replaces `Ctrl`. The command palette (`Ctrl+K`) finds any action
or screen by typing part of its name, and shows each action's shortcut.

## General

| Keys | Action |
|---|---|
| `Ctrl+K` | Command palette: every action and screen |
| `Ctrl+F` | Search |
| `Ctrl+Z` | Undo |
| `Ctrl+Y` (Windows, Linux), `⌘⇧Z` (macOS) | Redo |
| `Ctrl+Shift+Z` | Redo (alternative) |
| `Ctrl+B` | Collapse or expand the sidebar |
| `Ctrl+,` | Settings |
| `Ctrl+O` | Open a budget file |
| `Ctrl+1` … `Ctrl+7` | Go to Home, Budget, Review, Bills, Goals, Reports, All accounts |
| `Ctrl+Q` (macOS, Linux) | Quit Keel (Windows: `Alt+F4`) |

## Account register

| Keys | Action |
|---|---|
| `N` | New transaction (register) |
| `Enter` | Edit selected transaction |
| `C` | Toggle cleared |
| `A` | Approve |
| `Delete` | Delete with undo |
| `Ctrl+Enter` | Save and add another |
| `Esc` | Cancel editing |

## Budget

| Keys | Action |
|---|---|
| `Alt+←` | Budget: previous month |
| `Alt+→` | Budget: next month |
| `↑ ↓ ← →` | Budget: move between cells |
| `Enter` | Budget: edit the selected cell; save and move down |
| `Tab` / `Shift+Tab` | Budget: save and edit the next Assigned (previous with Shift) |
| a digit | Start typing an amount in Assigned |
| `M` | Budget: move money |
| `T` | Budget: set target |
| `Ctrl+Shift+F` | Budget: fund targets |
| `I` | Budget: show or hide the inspector |
| `Q` | Budget: quick assign |

## Review

| Keys | Action |
|---|---|
| `A` | Review: approve |
| `1`–`9` | Review: pick a suggestion |
| `C` | Review: change category |
| `S` | Review: split |
| `T` | Review: mark as transfer |
| `R` | Review: create rule |
| `D` | Review: delete |
| `J` / `K` | Review: next / previous |

## Bills

| Keys | Action |
|---|---|
| `1` / `2` / `3` | Calendar, list or subscriptions |
| `N` | Add a recurring bill or income |
| `R` | Run detection now |
| `Esc` | Close the detail panel |

## Reports

| Keys | Action |
|---|---|
| `1`–`4` | Choose a report |
| `Ctrl+E` | Export the report as CSV |

## Goals

| Keys | Action |
|---|---|
| `N` | New goal |

## Bank connections

| Keys | Action |
|---|---|
| `Ctrl+Shift+S` | Sync all linked accounts |

## Dialogs and amounts

| Keys | Action |
|---|---|
| `Enter` | Save or confirm |
| `Esc` | Cancel or close |
| `+ − × ÷` | Arithmetic in amount boxes (e.g. 12.50+3) |

Page keys (single letters and digits) work when no text box has the focus, so typing never triggers
them.

## The command palette

`Ctrl+K` lists every action that is not about one selected row, on every screen: pages and reports,
Bills tabs and months, budget months, the inspector, quick assign, Explain Ready to Assign and Manage
categories, batch approval in Review, new and apply-all rules, reconcile, edit account, record balance,
sync and reconnect for the register on screen, clear filters, new scheduled transaction, file actions
(new, open, move, import, back up, restore, integrity check, diagnostics, encrypt, remove encryption,
unlock), theme, density, accent colour and motion, notifications, bank connections and updates. Type
part of a name; `↑`/`↓` move, `Enter` runs, `Esc` closes.

Actions that do not apply right now (for example **Reconcile this account…** when no account register
is open, or **Remove encryption…** on a plain file) stay in the list, marked "Unavailable here", and do
not run. Actions on one selected row (toggle cleared, approve, the Review decisions) are keys and
buttons only, listed in the tables above.
