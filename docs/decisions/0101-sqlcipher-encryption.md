# 101. Optional SQLCipher encryption of budget files

- Status: Accepted
- Date: 2026-09-25
- Milestone: M9 (stream E)

## Context

F-SET-4 (P1) asks for an optionally SQLCipher-encrypted budget file with a user passphrase whose key the
user may choose to keep in the OS secret store (PRD 6.7 lists "optional SQLCipher key" among the secrets);
without the opt-in the file stays plain SQLite. PRD 7.1 pins `Microsoft.EntityFrameworkCore.Sqlite`, whose
dependency `SQLitePCLRaw.bundle_e_sqlite3` ships a SQLite build without encryption. Backups, restore, "Change
location", the integrity check and the diagnostic bundle (ADR 0081) open their own connections, and a
session (ADR 0080) is one host over one open file.

## Decision

- **Library substitution.** `Keel.Infrastructure` references `Microsoft.EntityFrameworkCore.Sqlite.Core`
  10.0.12 (the provider without a bundled SQLite) plus **`SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11**, pinned in
  `Directory.Packages.props`, instead of `Microsoft.EntityFrameworkCore.Sqlite`. It is the SQLCipher build that
  Microsoft.Data.Sqlite recognizes for its `Password` keyword, with native libraries for win-x64/x86/arm64,
  osx-x64/arm64 and linux-x64/arm64 (glibc and musl). `SQLitePCLRaw.core` stays on 2.1.12 (from
  Microsoft.Data.Sqlite.Core); the 2.1.x provider interface is the same. 2.1.11 is the last release of the
  bundle: SQLitePCLRaw 3.x dropped the e_sqlcipher bundles, so a later upgrade goes to SQLite3 Multiple
  Ciphers (which reads SQLCipher 4 files) and needs its own ADR.
- **It applies to plain files too.** Every file, encrypted or not, is now opened by SQLCipher 4.5.2, which is
  SQLite **3.39.2** (e_sqlite3 2.1.12 is 3.49). Without a key SQLCipher is plain SQLite and reads and writes
  the same file format, so existing files are untouched. The whole test suite (1,788 tests at the switch)
  passed unchanged on the older engine; `dotnet list package --vulnerable` reports nothing for the new
  packages. Measured on the 100k-transaction fixture (Linux, Release): register open 83 ms (102 ms with
  e_sqlite3), last page 180 ms (128 ms), review queue count 21 ms (21 ms): no meaningful difference.
- **Keys.** A file is a standard SQLCipher 4 database (AES-256-CBC, HMAC-SHA512, 4096-byte pages, a random
  16-byte salt in the first 16 bytes). Keel derives the key itself exactly as SQLCipher derives a passphrase
  (PBKDF2-HMAC-SHA512, 256,000 iterations, with the file's salt) once, and hands SQLCipher the raw key with the
  salt (`x'<64 hex key><32 hex salt>'`). This skips SQLCipher's per-connection derivation (about 0.3-0.5 s per
  new connection) and makes every copy made with the key (backups, moves) keep the salt, so the passphrase
  also opens the file, and its backups, in any SQLCipher tool. The salt in hex is the **file id**: it is not
  secret, readable without the key, and shared by copies.
- **Connections.** `KeelDatabase.ConnectionString(path, key)` sets Microsoft.Data.Sqlite's `Password`, which
  sends `PRAGMA key` as the first statement of every connection (before `SqlitePragmaInterceptor`); EF Core's
  read-only existence check copies the connection string, so read-only connections keep working. Connections
  to an encrypted file also get `PRAGMA cache_size = -65536` (up to 64 MiB of decrypted pages): with SQLite's
  default 2 MiB cache every page read from the OS cache must be decrypted again, and the 100k register took
  4-6 times as long (595 ms instead of 93 ms). The cache is a cap per pooled connection and fills only with
  pages actually read; plain files keep the default and the OS cache. The factory holds the key of the open
  file (`KeelDbContextFactory.CurrentKey`, internal); nothing logs a key or passphrase (tests capture every log
  line and the diagnostic bundle and search them), and `BudgetFileUnlock`/`BudgetFileEncryptionChange` override
  `ToString`.
- **Detection.** A file is encrypted when it exists, is a whole number of 512-byte pages long and does not
  start with `SQLite format 3\0`. Anything else (a text file renamed `.keel`) is left to SQLite to reject as
  "not a database" instead of asking for a passphrase.
- **Opening.** `IBudgetFileService.OpenOrCreateAsync(path, unlock, ct)` uses, in order, the passphrase typed,
  a key unlocked earlier in this app run (`BudgetFileKeyRing`, in memory, one instance shared by all sessions
  of the process like settings.json, keyed by file id, so a restore or move reopens without asking), then the
  key remembered in the OS secret store under `SecretKeys.BudgetFile(fileId)` = `budget-file/<salt hex>`. It
  stores the derived key, never the passphrase. No key, or a wrong one, throws `BudgetFileLockedException`
  (`WrongPassphrase` tells them apart); secret-store failures only log a warning.
- **Prompting.** At start a locked file does not fall back to another file: the session starts without a file
  (`AppSession.LockedFile`) and the shell shows the in-window `UnlockFileViewModel`; a wrong passphrase keeps
  the dialog open with an error, Cancel leaves the shell locked with "Unlock budget file…" in the palette and
  Settings. Switching files from Settings, the palette or a second launch asks in the current shell first
  (`BudgetSessions.OpenWithPromptAsync`), so Cancel keeps the current file; the first-run "Open existing" opens
  the file locked (`AllowLocked`) because the setup layer would hide a prompt.
- **Converting.** Encrypt and remove-encryption (`IBudgetFileEncryption.ConvertAsync`) run between sessions:
  `BudgetSessions.ChangeEncryptionAsync` stops the current session (no timer or pooled connection holds the
  file), converts in the new session before it opens the file, and reopens; when the conversion fails the file
  is reopened unchanged and the status strip says why. The conversion takes a verified backup first
  (`-before-encryption`, plain; `-before-decryption`, encrypted: new `BackupKind` values), writes the other
  form with `sqlcipher_export` inside one read transaction into `<file>.converting`, copies `user_version`,
  verifies the copy (`integrity_check`, known migrations, ledger tables, and equal row counts of transactions,
  audit events and settings), deletes the old WAL and shared-memory files (the export read through them) and
  moves the copy over the file. Removing the encryption asks for the current passphrase and checks it first.
  The remembered key of the old file is deleted from the secret store; the new one is stored only on request.
- **Backups, restore, moves, integrity, diagnostics.** Backups of an encrypted file are made with the SQLite
  backup API on two connections with the same key, so the zip holds an encrypted copy with the same key and
  salt; `backup.json` gains `"Encrypted": true` (older readers ignore it). Verification opens a copy with the
  file's key. A restored file always keeps the open file's encryption: when the backup is plain, or encrypted
  under another passphrase (which the user is asked for; `IBackupService.RestoreAsync(path, passphrase, ct)`),
  the extracted copy is first exported into the file's encryption, because the backup API cannot copy between
  a plain and an encrypted database or across keys. "Change location" copies with the key, so the moved file
  keeps key and salt. The integrity check opens the file with the key. The diagnostic summary adds
  `"encrypted": "yes"/"no"` and `cipher_version`, never a key.
- **Packaging.** The native library is `e_sqlcipher.dll`, `libe_sqlcipher.dylib` or `libe_sqlcipher.so`, copied
  into each RID's self-contained publish folder. `build/package.sh` and `build/package.ps1` now check that it is
  there before packing (the dry run prints the check), so a package without it fails instead of shipping an app
  that cannot open any file.

## Consequences

- Measured on the 100k fixture (`EncryptionTimingTests`, Linux, Release): file 78.2 MB plain, 74.6 MB
  encrypted (the export also compacts; SQLCipher reserves 80 bytes per 4 KiB page, about 2%); encrypting
  (verified backup, export, verification, swap) 6-9 s; opening 14-90 ms plain vs 250-320 ms encrypted (the
  key derivation); register open 93 vs 101 ms, last page 122 vs 121 ms, a budget month 298 vs 304 ms.
- Memory: each pooled connection to an encrypted file may grow its page cache up to 64 MiB, and only as far
  as the pages it reads (the 100k fixture is 75 MB in all). Idle memory with an encrypted 100k file is
  therefore higher than with a plain one; PRD 11's 250 MB idle budget has not been re-measured with an
  encrypted file (item in `docs/qa-checklist.md`). Plain files are unaffected.
- The Linux, Windows and macOS native libraries bundle their crypto (LibTomCrypt); `ldd` on
  `libe_sqlcipher.so` shows only libc, so the .deb and AppImage need no OpenSSL for it. A linux-x64
  self-contained publish is 120 MB unpacked, as before.
- Keel runs on SQLite 3.39.2 for every file until the SQLite3 Multiple Ciphers migration.
- Attachments are not encrypted (PRD 10); the user guide and Settings say so, and that a lost passphrase
  means lost data.
- Windows and macOS behaviour (DPAPI and Keychain for remembered keys, the native library per RID) is covered
  by the same code paths the Linux tests exercise; the release checklist has manual steps per OS.
