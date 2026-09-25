# 85. Command palette, shortcut registry and menus

- Status: Accepted
- Date: 2026-09-25
- Milestone: M8

## Context

F-SET-5 and PRD 9.1: a `Ctrl/Cmd+K` palette with a fuzzy list of every action and navigation target
and a keyboard shortcut reference. PRD 8: a native menu on macOS (About, Preferences, Quit) and an
in-window menu bar elsewhere. Shortcuts were listed by hand in Settings until M7.

## Decision

- **One registry** (`ShortcutRegistry`) lists every shortcut with a stable id, scope (General,
  Register, Budget, Review, Bills, Reports, Goals, Connections, Dialogs), action text and platform key
  text. Settings → Keyboard shortcuts is generated from it, grouped by scope; a test checks that every
  key binding on the main window is in it.
- **Commands** (`AppCommands`) are the palette's list and the menus' source: every sidebar page and
  account, every Settings section, file actions (new, open, move, import, backup, restore, integrity,
  diagnostics), edit and view actions (undo, redo, find, sidebar, theme, density) and each screen's
  main actions (add transaction, start review, fund targets, move money, bills detection and new item,
  new goal, export). A command shows the keys of the registry entry with the same id. Row-level
  actions that need a selected row (toggle cleared, approve a row) stay keyboard shortcuts only.
- **Fuzzy matching**: the query's characters in order, case-insensitive; the typed text found whole
  (prefix first, then at a word start) ranks above scattered letters; word starts and runs score
  higher; gaps lower; shorter titles win ties. Titles match before sections ("Go to", "Actions").
- **Page keys** (Bills 1/2/3, N, R, Esc; Goals N; Reports 1–4, Ctrl/Cmd+E) fire only when no text
  field has focus. Like Budget and Review, these pages take keyboard focus when shown, so their keys
  work right after `Ctrl/Cmd+1…7` or a sidebar click. New global keys: `Ctrl/Cmd+K` palette,
  `Ctrl/Cmd+,` settings, `Ctrl/Cmd+O` open file, `Ctrl/Cmd+Shift+S` sync all (listed under
  Connections), `Ctrl/Cmd+1…7` pages, `Ctrl/Cmd+Q` quit (not on Windows, which uses Alt+F4), and
  `Ctrl/Cmd+Shift+Z` redo besides the platform's redo.
- **Menus** are built from the same commands: File, Edit, View, Go, Help in the window on Windows and
  Linux; on macOS a `NativeMenu` with the app menu holding About and Preferences (macOS adds Hide and
  Quit), and Settings, Quit and About left out of File and Help.

## Consequences

- A new screen action is added once in `AppCommands` (and its key once in the registry); the palette,
  menus and the reference follow.
