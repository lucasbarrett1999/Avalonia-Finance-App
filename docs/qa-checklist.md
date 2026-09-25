# Manual QA checklist

Run before every release tag (PRD 13) on a real Windows, macOS and Linux machine, with the installers
from the release workflow (or `build/package.*`). Automated tests cover the logic and the headless UI;
this list covers what only a person on a real desktop can see: installers, OS integration, real
input, real displays and real time.

Record the version, OS and build, the date, and a pass/fail per line in the release PR.

## 0. Before you start

- [ ] CI is green on all three runners for the tagged commit; `dotnet format --verify-no-changes` passes.
- [ ] The tag equals the version in `Directory.Build.props`; `CHANGELOG.md` has the release's section.
- [ ] Use a fresh user account or remove the data folder (Windows `%APPDATA%\Keel`, macOS
      `~/Library/Application Support/Keel`, Linux `~/.local/share/keel`) to test a first run.

## 1. Install and first launch

- [ ] Windows x64 and arm64: `Setup.exe` installs without admin rights, creates Start menu and desktop
      shortcuts, the app starts; the icon shows in the taskbar and Alt+Tab.
- [ ] macOS arm64 and x64: the `.dmg` opens with Keel.app and an Applications link; the app starts
      after drag-install (Gatekeeper dialog as documented if unsigned); the `.pkg` also installs.
- [ ] Ubuntu 22.04 and 24.04: `sudo apt install ./keel_*.deb` pulls its dependencies; Keel appears in
      the app menu with its icon; `keel` starts it; `sudo apt remove keel` removes it.
- [ ] Linux AppImage: `chmod +x` and run; works on Wayland and X11.
- [ ] Installer sizes are under 80 MB (PRD 11).
- [ ] Cold start to the window is under 2 s on a 2020-class laptop (PRD 11).

## 2. First-run experience (PRD 9.10; time it)

- [ ] Welcome shows; nothing exists in `budgets/` until you choose.
- [ ] Create a file with a custom name; "Choose another folder…" opens the native save dialog.
- [ ] Pick a template; categories appear in Budget later. "Start empty" creates none.
- [ ] Add a checking account with a balance; Budget opens with Ready to Assign equal to the balance.
- [ ] "How bank connections work" explains the keys; "Read the bank sync guide" opens the browser;
      "Open Settings → Connections" lands on Connections.
- [ ] Home shows "Get started" with 3 of 4 done; assign all the money; the fourth step ticks off.
- [ ] Time from first launch to a fully assigned month is under 15 minutes (G1).
- [ ] Restart: the setup does not show again; the same file opens.
- [ ] (M9) On a fresh data folder, "Restore from a Keel export bundle…" with a bundle from another
      machine: the details (source file, date, counts) show; "Restore and open" creates the named file
      and opens it with every account, the Budget and Reports matching the source.
- [ ] (M9) On a fresh data folder, "Import from YNAB or Monarch export…" with a real YNAB Register
      export and a real Monarch export (each in turn): the preview lists every account with a sensible
      type, categories and tags to create, and a result line; Import lands on Budget; balances match
      the other app; transfers between the imported accounts are linked; Undo removes everything.

## 3. Budget file management (F-SET-1, PRD 8)

- [ ] Settings → General shows the file path, data, backups and logs folders.
- [ ] Back up now: a zip appears in `budgets/backups`, listed with date, kind and size.
- [ ] Restore that backup after changing data: confirmation first; data returns; a "before restore"
      backup is listed.
- [ ] Move budget file… into another folder (and a synced folder such as Dropbox/iCloud/OneDrive):
      the app keeps working from the new place; the old file is gone; attachments moved.
- [ ] New budget file… and Open budget file… switch files; the title bar shows the new name; undo
      history does not carry over.
- [ ] Double-click a `.keel` file in Explorer/Finder/Files: Keel opens it. With Keel already running,
      the running window comes to the front and switches to that file (single instance).
- [ ] `Keel <path>.keel` from a terminal opens that file.
- [ ] Automatic backup: with the clock moved to the next day (or waiting a day), one `-auto` backup
      appears after about 20 s; keep-N prunes older automatic backups only.
- [ ] Copy diagnostic bundle: the zip path is on the clipboard; the zip has logs and
      `schema-summary.json` with no payee names, amounts or notes (search it for a payee you entered).
- [ ] Open a copy of a damaged file (truncate a copy): Keel reports the problem in the status strip
      and keeps running.
- [ ] (M9) Export… as CSV zip, CSV folder and Keel bundle: the zip and folder hold the nine CSV files;
      open `transactions.csv` in a spreadsheet (UTF-8, ISO dates, decimals, split lines with Parent Id);
      the bundle is a single `.json`. The app stays responsive during a large export.
- [ ] (M9) Import bundle into a new file… with that bundle: the new file opens with the same accounts,
      balances, budget, rules, targets, schedules, bills and attachments; the original file is unchanged.
      A bundle edited by hand to reference a missing account is refused with a clear message and no
      file is left behind.
- [ ] (M9) Import the YNAB Budget/Plan export after the register: Assigned per month matches YNAB;
      importing it again changes nothing.

## 4. Everyday flows with the mouse and the keyboard

- [ ] Add, edit, split, transfer, delete (with undo) transactions in a register using only the keyboard.
- [ ] Import a CSV and an OFX from your own bank; re-import the same file: 0 new rows.
- [ ] Review: approve with `A`, `1`–`9`, change category with `C`, create a rule with `R`.
- [ ] Budget: assign with typing and Tab, move money with `M` and by dragging a pill, `Alt+←/→`.
- [ ] Bills: confirm a detected item; add a scheduled transaction; the bell shows alerts.
- [ ] Reports: click a donut slice down to the register; Export CSV opens in a spreadsheet.
- [ ] Command palette `Ctrl/⌘+K`: "backup", "budget", an account name, "dark" all work.
- [ ] `Ctrl/⌘+1…7`, `Ctrl/⌘+,`, `Ctrl/⌘+O`, `Ctrl/⌘+F`, `Ctrl/⌘+Z` and redo behave as listed in
      Settings → Keyboard shortcuts; on macOS the menus show ⌘ glyphs.
- [ ] Menus: Windows/Linux in-window File, Edit, View, Go, Help; macOS menu bar with the app menu
      holding About, Preferences and Quit.
- [ ] Tags (F-TXN-8): `T` on a row, type a new and an existing tag with `Enter`, remove one with
      `Backspace`, save; chips show in the row; the tag filter and `tag:`/`has:tag` find it; one undo
      removes the new tag. Settings → Tags: rename, merge, delete (count in the confirmation), undo each;
      "Flagged" cannot be renamed; a rule that adds a renamed tag uses the new name.
- [ ] Attachments: attach a PDF and a photo from the picker and by dragging them from the file manager
      onto the editor (a new and an existing transaction); the paperclip count shows; clicking opens the
      file in the system's viewer (Windows, macOS, Linux); remove and undo; the files are in
      `<name>.keel-attachments`, a backup zip contains them, Restore and Move budget file bring them along.
- [ ] Payee merge (F-TXN-9): check three spellings of one payee in Settings → Payees, Merge into…, check
      the counts, merge; the register, a scheduled transaction and a Bills item show the survivor; undo.

## 5. Appearance and accessibility (PRD 8, 9.11, 11)

- [ ] Light, dark and system themes; switching the OS theme follows it in "Match system".
- [ ] Each accent color; compact density; both persist after restart.
- [ ] Format override (e.g. German): amounts and dates reformat after the file reopens.
- [ ] OS reduce motion on (GNOME "Animations" off, macOS "Reduce motion"): the sync spinner does not
      turn; Settings → Appearance → Motion "Reduce motion" does the same on Windows.
- [ ] 200% display scaling (and a 1080p screen at 200%): nothing is cut off; Budget moves the Ready to
      Assign pill under the month and the inspector floats; text is crisp.
- [ ] Screen reader (Narrator, VoiceOver, Orca): the sidebar, top bar buttons, Budget cells, register
      rows, Review actions, dialogs and Settings controls are announced with names.
- [ ] Every screen can be used without a mouse; focus is always visible.

## 6. Bank sync (when keys are available)

- [ ] Plaid sandbox link with `user_good`/`pass_good`, initial and incremental sync, reconnect state,
      unlink keeps transactions (see `docs/user-guide/bank-sync.md`).
- [ ] SimpleFIN token claim and sync.
- [ ] Keys and tokens are in the OS secret store (DPAPI-encrypted files in `secrets/` on Windows,
      the Keychain on macOS, Secret Service on Linux) and nowhere in the data folder in plain text.

## 7. Updates and network

- [ ] With Settings → Updates off (default), no network traffic at startup (watch with a firewall or
      `tcpdump`); only bank sync talks to the network.
- [ ] With it on, an installed older version finds the release and installs it on restart (Windows,
      macOS, AppImage). The .deb and development builds say they cannot update themselves.

## 8. Upgrade

- [ ] Install the previous release, create data, then install this one over it: the file opens, a
      `-before-migration` backup exists if the schema changed, and nothing is lost.
- [ ] Open a file from this release with the previous release: it refuses with a clear message.
