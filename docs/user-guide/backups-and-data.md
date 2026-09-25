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
(encrypted bank credentials). Unless you encrypt it (see [Encryption](#encryption) below), the budget
file is a plain SQLite database: keep it somewhere only you can read.

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

## Encryption

You can encrypt a budget file with a passphrase. The file is then an SQLCipher database (AES-256):
without the passphrase its contents are unreadable, to other programs and to Keel alike.

> **A lost passphrase means lost data.** Nobody can reset or recover it, not Keel and not its
> developers. Backups of an encrypted file are encrypted with the same passphrase, so they do not help
> either. Write the passphrase down somewhere safe, or keep it in a password manager.

- **Encrypt this file…** (Settings → General → Encryption, or the command palette) asks for a
  passphrase twice (at least 8 characters; a few unrelated words work well) and for you to confirm the
  warning above. Keel first saves a **plain** backup (`…-before-encryption.zip`), then writes an
  encrypted copy, checks it by opening it, and replaces the file. Delete that backup from
  `budgets/backups` if a plain copy must not stay on your computer.
- **Opening an encrypted file** asks for the passphrase in the window. A wrong passphrase is refused
  with a message and you can try again; **Cancel** leaves the file locked (choose **Unlock budget
  file…** in the palette or in Settings → General to try again). Within one run of Keel, reopening the
  file (after a restore or a move) does not ask again.
- **Remember the key on this computer**: tick it in the passphrase prompt or in Settings → General,
  and Keel keeps the file's key in your system's secret store (Windows Data Protection, the macOS
  Keychain, or the Linux keyring; on Linux without a keyring, Keel's encrypted fallback file, which
  Settings marks as weaker). Keel then opens the file without asking. The store holds a key derived
  from the passphrase, never the passphrase itself. Untick it to make Keel ask again.
- **Remove encryption…** asks for the current passphrase, saves an encrypted
  `…-before-decryption.zip` backup, and writes the file back as plain SQLite.
- **Backups, restore and moves** keep the encryption: a backup of an encrypted file is encrypted with
  the same passphrase, and a moved file keeps it. Restoring always keeps the open file's encryption:
  a plain backup restored into an encrypted file is encrypted with the file's passphrase, and a backup
  made under an earlier passphrase asks for that passphrase first.
- The file stays a standard SQLCipher 4 database, so tools such as DB Browser for SQLite (SQLCipher
  edition) open it with the same passphrase.
- **Not encrypted**: the attachments folder next to the file, `settings.json`, and the logs (which
  never contain amounts or payees anyway).
- Encryption costs little: on a 100,000-transaction file the register and budget open as fast as
  a plain file's; opening the file takes about a quarter of a second longer while the key is derived.

## Privacy & Stats

**Settings → Privacy & Stats** shows how Keel is doing against its own goals, computed on your
computer from the open file's change history and from `settings.json`; nothing is ever sent anywhere.
Each card shows the value, the goal, whether it is met (with an icon and words, not only colour),
and how it is computed:

| Metric | Goal | Where it comes from |
|---|---|---|
| Time from first launch to first assigned budget | under 15 minutes | the first start of Keel on this computer (or, for older installs, the file's first change) to the first money assigned |
| Imported transactions approved without a change (last 30 days) | above 85% | approvals whose category was still the one Keel gave the transaction on import |
| Rows flagged as duplicates when a file is imported again | 100% | re-imports of the same file; Keel keeps a one-way fingerprint of each imported file, not its contents |
| Cold start to interactive | under 2 s with 100,000 transactions | measured at each start, from launch to the first frame with the file loaded |
| Register scroll at 100,000 rows | 60 fps (16.7 ms per frame) | frame times while you scroll a register of at least 100,000 rows |

A card says "Not measured yet" until there is something to measure. **Refresh** recomputes them.

## Diagnostics

**Copy diagnostic bundle** writes a zip to the data folder's `diagnostics/` and copies its path. It
contains Keel's logs and a summary of the file's structure: table and column names, indexes, row
counts, and SQLite settings. It contains no transactions, payees, amounts, notes or account names,
and the logs never record them either. Attach it to a bug report if you like.

## Privacy

- Keel sends no telemetry and needs no account.
- It connects to the internet only when you sync with a bank provider you set up, and to check for
  updates if you switch that on (**Settings → Updates**, off by default).
- Bank keys and tokens, and a remembered encryption key, are stored in your system's secret store,
  never in the budget file.
- The Stats page is computed locally and never transmitted.
