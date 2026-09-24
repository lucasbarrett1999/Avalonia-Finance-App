# 72. Plaid and SimpleFIN providers

- Status: Accepted
- Date: 2026-09-24
- Milestone: M7

## Context

PRD 7.5 fixes Plaid Hosted Link with `/link/token/get` polling (2 s, up to 10 min) and
`/transactions/sync`; F-TXN-3 adds SimpleFIN Bridge as P1. Keys are the user's own (PRD 14 D2).

## Decision

### Plaid

- Going.Plaid 6.67.0 behind `IPlaidApi` (`GoingPlaidApi`: one `PlaidClient` per environment, HTTP
  from the `PlaidClient` named client with `AddStandardResilienceHandler`: 3 retries on transient
  errors, 60 s per attempt, 4 min in total). Credentials go in each request body; Going.Plaid takes
  no cancellation token, so calls stop waiting on cancellation. Tests fake the HTTP layer underneath.
- `/link/token/create`: `hosted_link: {}`, products `transactions`, `transactions.days_requested`
  90, countries US and CA, English, `client_user_id` "keel-local-user" (one local user per set of
  keys; no personal data). Update mode sends the item's `access_token` and no products.
- Completion: a new link succeeds when a session has an `item_add_results` public token (exchanged,
  stored with its environment and item id); an update-mode link succeeds when a session finishes
  without an exit (update mode returns no public token); all sessions finished with an exit means the
  user closed Link; the deadline is 10 minutes or the token's expiry.
- Transactions: the sign flips (Plaid amounts are positive for money out); minor units follow the
  ISO currency (unofficial codes such as crypto fall back to the account's currency); the payee is
  `merchant_name`, else `name`; the memo is empty; `pending` and `pending_transaction_id` are passed
  through. History is complete at `HISTORICAL_UPDATE_COMPLETE`.
- Balances and account lists come from `/accounts/get` (cached balances; `/accounts/balance/get` is a
  paid product and not needed for a daily sync).
- Health: `ITEM_LOGIN_REQUIRED`, `PENDING_EXPIRATION`, `PENDING_DISCONNECT`, `ACCESS_NOT_GRANTED`,
  credential and MFA errors → `NeedsReauth`; everything else → `Error` with Plaid's code.
- Unlink: `/item/remove` (an item Plaid no longer knows counts as removed), then the token is deleted.

### SimpleFIN Bridge

- The setup token (Settings, secret store, single use) is base64 of an https claim URL; a POST
  returns the access URL, stored as the connection's secret; the token is then deleted. SimpleFIN
  links need no browser (`LinkSession.OpensBrowser = false`).
- Credentials in the access URL are sent as a Basic header to the URL without them; the SimpleFIN
  HTTP client has its request logging removed so no URL with a token reaches the logs.
- SimpleFIN has no cursor and no removals: the stored "cursor" is the Unix time of the last fetch,
  and each sync reads `start-date` 14 days before it (90 days the first time) with `pending=1`;
  provider ids dedup the overlap and update changed rows. Account types are guessed from names
  (SimpleFIN reports none). 401/403 → `NeedsReauth` (`SIMPLEFIN_ACCESS_REVOKED`), 402 → `Error`,
  bridge errors with no accounts → `NeedsReauth`.
- "Reconnect" opens the bridge website and polls until the accounts read again; unlink forgets the
  access URL (SimpleFIN has no revoke call).

## Consequences

- The gated sandbox test (`KEEL_PLAID_CLIENT_ID`, `KEEL_PLAID_SECRET`) creates its item with
  `/sandbox/public_token/create`, because Hosted Link needs a person in a browser; everything after the
  exchange runs through the provider and the sync service.
- Rows removed by SimpleFIN at the bank are not removed in Keel.
