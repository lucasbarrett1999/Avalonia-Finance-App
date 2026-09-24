# Bank sync

Keel works fully without a bank connection: manual entry and file import (CSV, OFX/QFX, QIF) are
first-class. Bank sync is optional and pulls transactions and balances from your bank through a
provider. Two providers are built in:

| Provider | What you need | Coverage |
|---|---|---|
| **Plaid** | Your own Plaid developer keys (client ID and secret) | Most US and Canadian banks |
| **SimpleFIN Bridge** | A SimpleFIN Bridge account and a setup token; no developer keys | Banks supported by the bridge |

Everything you enter here stays on your computer. Keys and access tokens live in your operating
system's secret store, never in the budget file, `settings.json` or the logs.

## Security first: bring your own keys

Keel uses **your own** Plaid keys. Do not put a shared production secret into a copy of Keel that
you give to other people: anyone who has the app could extract the secret and use your Plaid
account. Each person should create their own Plaid developer account and enter their own keys. A
hosted token-exchange proxy that needs no keys is on the roadmap (PRD 14, D2), not in v1.

## Plaid

### 1. Get keys

1. Create a free account at <https://dashboard.plaid.com/signup>.
2. In the dashboard, open **Developers → Keys**. Copy the **client_id** and the **Sandbox** secret.
   (Production access needs Plaid's approval; its secret is listed on the same page once granted.)
3. Under **Developers → API → Allowed redirect URIs** nothing is needed: Keel uses Plaid Hosted Link,
   which runs on Plaid's own pages in your browser.

### 2. Enter them in Keel

Open **Settings → Connections**:

1. Paste the client ID and choose **Save**; paste the secret and choose **Save**. The boxes hide
   what you type, and once saved the values are shown only as dots with a **Replace** button.
2. Choose the **Environment** that matches the secret: **Sandbox** for test banks, **Production**
   for your real bank.

### 3. Link a bank

1. Choose **Add connection**, pick **Plaid**, then **Continue in browser**. Keel opens Plaid's
   Hosted Link page in your default browser and waits (up to 10 minutes). The dialog shows the
   link in case the browser did not open; **Cancel** stops waiting.
2. Pick your bank and sign in on Plaid's page. Keel never sees your bank password.
3. Back in Keel, the **Link accounts** dialog lists the bank's accounts. For each one choose:
   - **Create a new account** (name and type are prefilled). Once the history has synced, the new
     account gets a cleared "Starting Balance" so its balance matches the bank.
   - **Link to** an existing Keel account (for example one you imported files into). Nothing is
     added to it except the synced transactions; matching manual entries are matched, not duplicated.
   - **Don't import**.
4. Choose **Link accounts**. Keel syncs straight away.

**Sandbox test login:** in the Sandbox environment, pick any test institution (for example
"First Platypus Bank") and sign in with username **`user_good`** and password **`pass_good`**.

### Syncing

- **Sync all** in the top bar syncs every connection and shows when the last sync finished.
- An account's register has a **Sync** button when the account is linked.
- Keel syncs when it opens the file and every few hours while it runs (Settings → Connections →
  *Sync automatically*; choose *Only when I ask* to turn this off).
- Progress and results appear in the status bar at the bottom; syncing never blocks the app.
- New transactions arrive unapproved in the review queue and go through the same payee cleanup,
  duplicate detection, rules and categorization as file imports. Pending transactions are
  uncleared; when the bank posts them, the same row is updated (your category and memo stay).
- The bank's reported balance appears in the register next to the cleared balance; if they differ,
  the register suggests reconciling.
- Transactions the bank removes are deleted in Keel (undo brings them back); reconciled
  transactions are never changed by a sync.

### When a connection breaks

A broken connection is a state, not an error dialog: the account's dot in the sidebar turns amber
(needs you) or red (sync failed), the register shows a banner, and everything else keeps working.

- **Sign-in needed** (Plaid `ITEM_LOGIN_REQUIRED`, for example after a password change): choose
  **Reconnect** in the banner or in Settings → Connections. Plaid opens in the browser in update
  mode; sign in again and syncing resumes.
- **Sync failed**: the reason is shown (for example "the bank is not available right now"). Keel
  tries again at the next sync; **Try again** / **Sync now** retries at once.

### Unlink

Settings → Connections → **Unlink** removes the item at Plaid and deletes its access token from the
secret store. The accounts become manual accounts and **every transaction already imported stays**.

## SimpleFIN Bridge

1. Create an account at <https://beta-bridge.simplefin.org/> and connect your banks there.
2. In the bridge, create a **setup token** for a new app and copy it.
3. In Keel, Settings → Connections → *SimpleFIN Bridge setup token*: paste it and choose **Save**.
4. Choose **Add connection → SimpleFIN Bridge → Continue**. Keel exchanges the token (it can be used
   only once) for an access URL, stores that in the secret store, and shows the account mapping.

SimpleFIN has no update mode: if a bank needs attention, fix it at the bridge website, then sync
again. If the bridge revokes Keel's access, create a new setup token and link again. Unlink forgets
the access URL; you can also remove the app at the bridge.

## Where secrets are stored

Settings → Connections shows which store is in use:

| OS | Store |
|---|---|
| Windows | DPAPI (current user), encrypted files in `%APPDATA%\Keel\secrets\` |
| macOS | The login Keychain, service `com.keel.app` |
| Linux | The Secret Service over D-Bus (GNOME Keyring, KWallet, KeePassXC), items with `service=com.keel.app` |
| Linux without a Secret Service | An AES-GCM encrypted file in `~/.local/share/keel/secrets/`, keyed from `/etc/machine-id` and your user name (Argon2id) |

The Linux file fallback is **weaker**, and Settings says so: anyone who can read your files and
knows the machine id can decrypt it. Install and unlock a keyring (for example `gnome-keyring`) to
use the Secret Service instead.

Access tokens belong to the machine's secret store, not to the budget file. If you open the file on
another computer, its connections show *Sign-in needed*; reconnect or link them again there.

## Troubleshooting

| Message | What to do |
|---|---|
| Plaid rejected the client ID or secret | Check both keys and that the Environment matches the secret |
| Enter your Plaid keys in Settings → Connections | Keys are missing on this machine |
| The link timed out / was closed | Start **Add connection** again |
| The OS secret store could not be read | Unlock your keyring (Linux) or Keychain (macOS) |
| The provider could not be reached | Check the internet connection; Keel retries transient errors |
