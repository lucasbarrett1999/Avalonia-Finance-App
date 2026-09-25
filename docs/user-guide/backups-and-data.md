# Backups and the data file

## Where your data is

Everything about a budget is in one file with the `.keel` extension (an SQLite database), plus a
`<name>.keel-attachments` folder next to it when you attach files. **Settings → General** shows the
file's full path, and the data folder:

| System | Data folder |
|---|---|
| Windows | `%APPDATA%\Keel` |
| macOS | `~/Library/Application Support/Keel` |
| Linux | `~/.local/share/keel` (or `$XDG_DATA_HOME/keel`) |

The data folder holds `settings.json` (theme, window size, the last file you opened), `logs/`,
`budgets/` (budget files and `backups/`) and, on Windows and when Linux has no keyring, `secrets/`
(encrypted bank credentials). The budget file is not encrypted; keep it somewhere only you can read.

## Files

In **Settings → General** (and the File menu):

- **New budget file…** creates another file and walks you through categories and a first account.
- **Open budget file…** opens another `.keel` file. Keel remembers the last file you opened.
  Double-clicking a `.keel` file opens it too; if Keel is already running, the running window switches
  to that file.
- **Move budget file…** moves the file and its attachments, for example into a synced folder. Keel
  copies the file, checks the copy, opens it and then removes the old one.

A file created by a newer version of Keel is refused with a message instead of being changed.
Only one Keel window runs at a time.

## Backups

- **Back up now** writes a zip with the budget file and its attachments to `budgets/backups`, named
  `Name-YYYYMMDD-HHMMSS.zip`, and checks it by opening the copy.
- **Automatic backups** (on by default) make one backup a day and keep the newest N (10 by default,
  set in Settings → General). Keel also backs up before updating a file's format and before a restore;
  those are kept until you delete them.
- Backups are on the same disk as your data. Copy the backups folder (or the `.keel` file while
  Keel is closed) to another drive or cloud storage now and then.

## Restore

Choose **Restore** next to a backup in the list, or **Restore from a file…** for a zip elsewhere. Keel
asks first, saves the current state as a "before restore" backup, replaces the file and its attachments
with the backup's, and reopens it. Damaged backups and backups from a newer Keel are refused.

## The integrity check

Once a day Keel runs SQLite's `integrity_check` on the open file. If it finds damage, the status strip
says so in red: restore the most recent good backup. **Check now** runs it on demand. While the file
fails the check, Keel does not make automatic backups, so good backups are not replaced.

## Diagnostics

**Copy diagnostic bundle** writes a zip to the data folder's `diagnostics/` and copies its path. It
contains Keel's logs and a summary of the file's structure: table and column names, indexes, row
counts, and SQLite settings. It contains no transactions, payees, amounts, notes or account names,
and the logs never record them either. Attach it to a bug report if you like.

## Privacy

- Keel sends no telemetry and needs no account.
- It connects to the internet only when you sync with a bank provider you set up, and to check for
  updates if you switch that on (**Settings → Updates**, off by default).
- Bank keys and tokens are stored in your system's secret store, never in the budget file.
