# 80. Budget-file sessions

- Status: Accepted
- Date: 2026-09-25
- Milestone: M8

## Context

F-SET-1 asks for Create new, Open existing, Change location and Restore from backup, and PRD 8 asks a
second launch to open the `.keel` file it was given. Until M7 the app opened one file at startup and
every service, cache (learner, forecast, budget ledger data), undo history and view model lived for
the whole process. Switching files in place would mean resetting each of them, and any one missed
would show or write the previous file's data.

## Decision

- **A session is one generic host over one open budget file** (`BudgetSessions`, Desktop/Services).
  Opening, creating, moving or restoring a file builds a new host with the same registrations, opens
  the file in it (`BudgetFileStartup` with `BudgetStartupOptions`), moves the one main window to the
  new shell (`ShellWindow.Attach`: data context, key bindings, menus; window placement and sidebar
  width carry over), and then stops and disposes the previous host. Nothing of the previous file
  survives: caches, timers (`RecurringJobs`, `SyncCoordinator`, `MaintenanceJobs` are disposed),
  undo history and view models go with its container.
- **If the new file cannot be opened** (not a Keel file, made by a newer Keel, unreadable) the new
  host is discarded and the current session keeps running; the status strip says why.
- **One messenger per session** (`WeakReferenceMessenger` instance instead of the process-wide
  default), so a closed session's screens never receive the next file's `LedgerChanged`.
- **settings.json is shared**: one `IAppSettingsStore` instance is registered in every session.
- **Format culture changes reopen the file** in a new session so every screen formats again (ADR 0082).
- **Single instance** (PRD 8): the first process holds `instance.lock` in the data directory and
  listens on a local pipe named from the data directory and user (a named pipe on Windows, a Unix
  domain socket elsewhere, both through `NamedPipeServerStream`). A second launch sends `open\t<path>`
  or `activate` and exits; the first brings its window forward and opens the file through
  `BudgetSessions.ActivateAsync`. When nobody answers within 3 s the launch starts normally. The first
  `.keel` argument (a path or `file://` URI) is the file to open; macOS delivers double-clicked files
  as an activation event, which goes to the same method.
- Tests keep `App.Services` unset: a session switch replaces it only when it pointed at the previous
  session (the real app), so a disposed container never leaks into the next headless test.

## Consequences

- A file switch costs a host start (about the cost of app startup minus the UI), which is acceptable
  for an explicit user action.
- Code that caches file data must live in a session-scoped service, never in a static.
- `TestHost.Current<T>()` resolves from the running session; `TestHost.Get<T>()` stays on the first.
