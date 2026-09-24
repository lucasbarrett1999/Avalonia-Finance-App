# 70. OS secret stores

- Status: Accepted
- Date: 2026-09-24
- Milestone: M7

## Context

PRD 6.7 (normative) asks for `ISecretStore` over DPAPI on Windows, the Keychain on macOS and the
Secret Service on Linux with an Argon2id/AES-GCM file fallback that Settings flags as weaker. Only
Linux can be exercised in the sandbox; Windows and macOS must compile behind OS guards.

## Decision

- **Selection.** `SecretStoreSelector` (the registered `ISecretStore` and `ISecretStoreInfo`) picks
  once, on first use: `OperatingSystem.IsWindows()` → DPAPI, `IsMacOS()` → Keychain, otherwise the
  Secret Service if one answers on the session bus within 3 s, else the encrypted file. Settings →
  Connections shows the backend and, for the file, the weaker-fallback warning. A store registered
  earlier in DI wins (tests use `InMemorySecretStore`; the desktop tests never touch a real keyring).
- **Key names** are plain, non-secret strings (`SecretKeys`): `plaid/client-id`, `plaid/secret`,
  `plaid/environment`, `simplefin/setup-token`, `connection/<id:N>`. A connection's value is a small
  JSON document (provider, Plaid environment, access token and item id, or SimpleFIN access URL), so a
  token always carries the environment it belongs to. `SyncConnection.SecretRef` holds the key name.
- **Windows.** One file per secret, `KDP1` magic + `ProtectedData.Protect(value, entropy,
  CurrentUser)`; the entropy is "Keel secret v1\n" + key name, so a blob cannot be moved to another
  key. Files live in the data directory's `secrets/` (PRD 7.4; `%APPDATA%\Keel\secrets`) rather than
  the `%LOCALAPPDATA%` path in PRD 6.7: one data root, and DPAPI user keys roam with the profile.
- **macOS.** Generic passwords (service `com.keel.app`, account = key name) through
  `SecItemCopyMatching`/`SecItemUpdate`/`SecItemAdd`/`SecItemDelete` with `LibraryImport`; constants
  are read from the framework exports. The attribute lists are built by the platform-neutral
  `KeychainQuery`, which is unit-tested on every OS; a real round trip runs with `KEEL_TEST_KEYCHAIN=1`.
- **Linux Secret Service.** A minimal client over `Tmds.DBus.Protocol` (the libsecret protocol:
  `OpenSession("plain")`, `ReadAlias("default")`, `SearchItems`, `Unlock` with the service's own
  prompt, `CreateItem(replace)`, `GetSecret`, `Item.Delete`). Items carry `xdg:schema=com.keel.app.Secret`,
  `service=com.keel.app`, `key=<name>`. The "plain" session algorithm is what libsecret uses when the
  bus is the user's own session bus.
- **Package pin.** `Tmds.DBus.Protocol` is pinned to **0.21.3**, not the latest (0.95.x):
  Avalonia.FreeDesktop 11.3 depends on 0.21.3, and 0.90+ renamed and removed types, so a newer
  version would be unified into the desktop app and break Avalonia's own D-Bus use on Linux.
  0.21.3 is the version with the signal-spoofing fix and is reported clean by the vulnerability scan.
- **Linux fallback.** One file per secret: `KEF1` magic, 12-byte nonce, 16-byte tag, AES-256-GCM
  ciphertext with the key name as associated data. The key is Argon2id (Konscious 1.3.1; .NET has no
  Argon2) over `machine-id + "\n" + user name` (`/etc/machine-id`, then `/var/lib/dbus/machine-id`,
  then the host name) with a random 16-byte salt; salt and cost (64 MiB, 3 passes, 1 lane) are in
  `secrets/fallback-key.json`, so later cost changes keep old files readable. The key is derived once
  per process. Files are mode 600 in a 700 directory and written through a temporary file and rename.
- File names are the SHA-256 of the key name, so key names never become paths.
- Nothing logs a key name or value; failures surface as `SecretStoreException` with a fixed message.

## Consequences

- The real Secret Service path is verified in the sandbox against GNOME Keyring on a private session
  bus (`KEEL_TEST_SECRET_SERVICE=1`); DPAPI round-trips run only on Windows CI; the Keychain needs an
  opt-in variable because CI keychains can prompt.
- Secrets are per machine: a budget file opened elsewhere shows its connections as needing a new
  sign-in (missing access token) until they are reconnected there.
- Unsigned macOS builds may see a Keychain prompt after an update changes the binary's signature.
