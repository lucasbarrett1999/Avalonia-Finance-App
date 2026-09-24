# Product Requirements Document: Keel

**Working name:** Keel (placeholder; rename is a find-and-replace)
**Document version:** 1.0, September 2026
**Audience:** the implementing agent (Claude Opus 5.5) and the project owner
**Companion:** `docs/competitive-analysis.md` (why these requirements)

---

## 0. How to use this document (read first)

This PRD is written to be executed by an autonomous coding agent. It is organized so that:

- **Section 1–4** give the product thesis, scope, and users. Read once.
- **Section 5** is the feature spec with priorities (P0 = must ship in v1, P1 = ship in v1 if time allows, P2 = post-v1). Each feature has acceptance criteria that double as test cases.
- **Section 6** is the domain model and the budgeting math. This is the most important section to implement exactly; ambiguity here creates data bugs.
- **Section 7–8** fix the architecture and stack. Do not substitute libraries without recording the reason in `docs/decisions/`.
- **Section 9** specifies every screen.
- **Section 12** is the build order. Follow it: each milestone leaves a runnable, tested app.
- **Section 14** lists decisions made under assumption. If the owner disagrees, those are the knobs.

**Repository reset:** the owner has authorized a full reset of this repository. Section 7.6 says what to keep from the current code (very little) and how to lay out the new solution. Do the reset as the first milestone.

**Definition of done for any task:** code compiles on Windows, macOS, and Linux targets (`dotnet build` in CI matrix), unit tests pass, the feature is reachable from the UI, and the acceptance criteria in this PRD for that feature are demonstrably met.

---

## 1. Product thesis

Keel is a **native, cross-platform desktop personal-finance application** (Windows, macOS, Linux) built with Avalonia and .NET. It combines the two things no competitor offers together:

1. **YNAB-grade envelope budgeting** (zero-based, targets, true expenses, credit-card handling, rollover), and
2. **Monarch-grade full picture** (net worth, investment balances, recurring bills, cash-flow forecast),

on top of a foundation none of them have at all:

3. **Local-first, user-owned data.** One SQLite file. Works offline. No account needed. Bank sync is optional and pluggable. File import and manual entry are first-class, not fallbacks.

The competitive analysis found that every leading app is a cloud subscription with bank-sync reliability as its top complaint, no real desktop client, and a forced choice between budgeting depth and financial overview. Keel is built for the person who wants control of both their money and their data, and who lives on a desktop.

### 1.1 Product principles (use these to break ties)

1. **The ledger is the truth.** Every number on every screen is derivable from transactions and budget assignments in the database. No cached totals that can drift.
2. **Every import path is equal.** Plaid sync, CSV/OFX import, and manual entry produce identical transaction records and go through the same dedup, rules, and review queue.
3. **Failure is a state, not an error dialog.** A broken bank connection shows as a status on the account with a "fix" action; the rest of the app keeps working.
4. **Keyboard first, mouse complete.** Every common action (add transaction, categorize, approve, move money) has a shortcut. Power users should never need the mouse for daily use.
5. **Fast at scale.** 100,000 transactions must feel instant: virtualized lists, indexed queries, no full-table loads on the UI thread.
6. **No dark patterns.** No upsell screens, no hidden fees, no cross-selling, no data leaving the machine without explicit opt-in.
7. **Show the math.** Any computed number (Available, To Assign, forecast balance) can be clicked to see how it was calculated.

---

## 2. Goals and non-goals

### 2.1 Goals (v1)

- G1. A user can set up a complete envelope budget in under 15 minutes with no bank connection (manual accounts + starting balances).
- G2. A user can import a year of transactions from CSV/OFX/QFX exports with correct deduplication and categorization on re-import.
- G3. A user with Plaid credentials can link institutions and sync transactions incrementally, with visible connection health.
- G4. Budget math matches the reference model in Section 6 exactly, including credit cards, overspending, and month rollover.
- G5. Net worth, spending, income vs expense, and a 90-day cash-flow forecast are available as reports.
- G6. Recurring transactions are detected and surfaced as a bill calendar with price-change alerts.
- G7. The application runs natively on Windows 10+, macOS 13+, and Ubuntu 22.04+ from one codebase, with light and dark themes.
- G8. All user data lives in one SQLite file the user can locate, back up, move, and export.

### 2.2 Non-goals (v1)

- No cloud backend, no user accounts, no multi-device real-time sync (see P2 household features).
- No bill negotiation, subscription cancellation service, credit score, or financial product recommendations. Ever.
- No mobile apps. (Avalonia can target mobile later; the architecture must not preclude it, but v1 is desktop only.)
- No investment holdings-level tracking (lots, cost basis, performance). Investment accounts are balance-tracked in v1 (P2 for holdings).
- No multi-currency budgeting. Accounts carry a currency code, but v1 budgets in a single base currency and refuses to mix.
- No AI assistant in v1. The categorization learner is local and statistical, not an LLM. (An optional, opt-in LLM-backed assistant is P2.)

---

## 3. Users and jobs to be done

### 3.1 Personas

**Priya, the switcher (primary).** Used YNAB for three years, tired of $109/year and sync breakage. Wants the envelope method, keyboard speed, and her data in a file she owns. Has ~8 accounts including two credit cards and a mortgage. Windows at work, Mac at home.

**Marcus, the full-picture tracker.** Left Mint for Monarch, likes net worth and investments but finds budgeting shallow and resents the Plus paywall. Doesn't want to categorize every transaction by hand. Linux desktop. Wants CSV import because his credit union never syncs.

**Dana and Sam, the couple (secondary, P2).** Share rent and groceries, keep separate fun money. Want "ours/mine/yours" visibility without a joint login on someone else's server.

### 3.2 Jobs to be done

- JTBD1. "When I get paid, I want to assign every dollar to a purpose so I know what I can spend."
- JTBD2. "When I spend, I want the transaction to show up categorized so I don't have to do bookkeeping."
- JTBD3. "When I'm about to make a purchase, I want to know what's actually available in that category right now."
- JTBD4. "At month end, I want to see where money went and whether I'm on track for my goals."
- JTBD5. "When a subscription price goes up or a trial converts, I want to know before it happens."
- JTBD6. "I want to know my net worth trend and what my checking balance will look like in 30, 60, 90 days."
- JTBD7. "When my bank connection breaks, I want to fix it in one click, or import a file, without losing anything."

---

## 4. Success metrics (instrumented locally, never transmitted)

These are for the owner to evaluate the product; they are computed locally and shown on a private "Stats" page in Settings.

- Time from first launch to first assigned budget (target: < 15 min).
- Percentage of transactions auto-categorized correctly (approved without change in review) after 30 days of use (target: > 85%).
- Percentage of imported transactions flagged as duplicates on re-import of the same file (target: 100%).
- Cold start to interactive with 100k transactions (target: < 2 s on a 2020 laptop).
- Transaction list scroll at 100k rows (target: 60 fps, no jank).

---

## 5. Feature requirements

Priority key: **P0** must ship in v1. **P1** ship in v1 if schedule allows; design for it regardless. **P2** post-v1; do not build, but do not preclude.

### 5.1 Ledger and accounts

**F-ACC-1 (P0) Account management.** Create, edit, close, reopen, and reorder accounts. Types: Checking, Savings, Cash, Credit Card, Line of Credit, Loan/Mortgage, Investment, Other Asset, Other Liability. Each account has: name, type, currency (ISO 4217), on-budget flag (see 6.2), opening balance and date, notes, and an optional link to a sync connection.

Acceptance:
- Creating an account with an opening balance creates a system "Starting Balance" transaction on the opening date, categorized to Inflow: Ready to Assign (asset accounts on budget) or uncategorized (tracking accounts).
- Closing an account requires a zero balance or an explicit "close with balance" confirmation; closed accounts hide from sidebars but remain in reports.
- Reordering persists and is per-account-type-group.

**F-ACC-2 (P0) Account register.** A virtualized transaction list per account (and an "All Accounts" register) with columns: date, payee, category, memo, outflow, inflow, cleared status, running balance. Inline editing of every field. Multi-select with bulk actions (categorize, approve, delete, mark cleared, move to account).

Acceptance:
- 100k rows scroll smoothly; opening the register with 100k rows takes < 500 ms after the DB is warm.
- Running balance column is correct after any edit, sort, or filter (it is computed from the DB for the visible ordering, not from the in-memory list).
- Keyboard: `N` new transaction, `Enter` edit, `Esc` cancel, `C` toggle cleared, `A` approve, `Ctrl/Cmd+Enter` save and new, `Delete` delete with undo toast.

**F-ACC-3 (P0) Reconciliation.** Enter the statement balance and date; the app shows the difference between cleared balance and statement balance; user clears/unclears until zero, then "Finish" locks cleared transactions as reconciled and records a reconciliation event. If the difference is nonzero the user may create a balance-adjustment transaction.

**F-ACC-4 (P0) Transfers.** A transfer is one logical record that appears in both registers. Editing one side updates the other. Transfers between on-budget accounts have no category. Transfers from on-budget to off-budget (tracking) accounts require a category (money is leaving the budget).

**F-ACC-5 (P0) Split transactions.** A transaction may have N sub-splits each with category, memo, and amount; the sum of splits must equal the parent amount. Splits show as expandable rows.

**F-ACC-6 (P1) Scheduled transactions.** A transaction can be scheduled with a recurrence rule (RFC 5545 subset: daily, weekly, every N weeks, monthly on day D or on Nth weekday, yearly, twice-monthly). Upcoming instances appear ghosted in the register and are "entered" automatically on their date (configurable: auto-enter or prompt). Feeds the forecast (F-REP-4).

### 5.2 Budget (the envelope engine)

**F-BUD-1 (P0) Category groups and categories.** Hierarchical: groups contain categories. System group "Inflow" contains "Ready to Assign". System group "Credit Card Payments" holds one auto-created category per credit-card account. User groups are freely created, renamed, reordered, hidden, and deleted (deleting a category with history requires re-assigning its transactions to another category).

**F-BUD-2 (P0) Monthly budget view.** Grid by month: for each category show Assigned (editable), Activity (computed), Available (computed). Group rows show sums. Header shows "Ready to Assign" for the month. Navigation: previous/next month, jump to month, and a 3-month side-by-side mode (P1).

Acceptance (see Section 6.4 for math):
- Editing Assigned updates Available for that month and all future months, and updates Ready to Assign, within one render frame.
- Overspent categories (Available < 0) show red; if the overspending is credit-based (see 6.4.5) they show yellow.
- "Ready to Assign" negative shows red with a banner "You've assigned more than you have."

**F-BUD-3 (P0) Move money.** Drag or dialog to move an amount from one category's Available to another (including to/from Ready to Assign). Undoable.

**F-BUD-4 (P0) Targets.** Per-category target types:
- *Monthly set-aside:* assign X every month.
- *Monthly spending:* have X available each month (refill to X).
- *Savings balance by date:* reach total X by date D (app computes monthly need).
- *Debt payment:* pay X per month toward a linked loan/credit account.
The budget view shows target progress and an "Underfunded by" amount; a "Fund targets" action assigns the needed amounts for all underfunded categories in the current month (limited by Ready to Assign, with a clear message when not enough).

**F-BUD-5 (P0) Quick-assign actions.** Per category: "Assigned last month", "Spent last month", "Average assigned (3 mo)", "Average spent (3 mo)", "Fund target", "Reset to zero". Available as a context menu and as a keyboard palette.

**F-BUD-6 (P1) Flex mode (one-number view).** An alternate presentation of the same data: categories are tagged Fixed, Non-monthly, or Flex. The Flex view shows total income, total fixed, total non-monthly set-aside, and one Flex number with spending progress. Assigned amounts still exist underneath; Flex mode only changes what the user looks at. No separate data model.

**F-BUD-7 (P1) Notes and month notes.** Free text per category and per month.

**F-BUD-8 (P1) Budget templates.** On first run offer starter category sets (Simple, Detailed, Student, Family). Applying a template creates groups/categories; it never deletes.

### 5.3 Transactions: import, sync, categorization

**F-TXN-1 (P0) Unified import pipeline.** All sources (manual, file import, bank sync) call the same `ImportTransactions(source, batch)` service which:
1. Normalizes payee (trim, collapse whitespace, strip card-processor noise like `POS DEBIT`, `SQ *`, `TST*`, trailing reference numbers).
2. Deduplicates (see 6.5).
3. Applies payee rename rules.
4. Applies categorization rules, then the learner (F-TXN-5) if no rule matched.
5. Detects transfers between the user's own accounts (matching opposite amounts within ±3 days).
6. Inserts as *unapproved* unless the source is manual entry.
7. Emits an import summary (added, duplicates skipped, transfers matched, uncategorized).

**F-TXN-2 (P0) File import.** Formats: CSV (with a column-mapping dialog that remembers mappings per account and auto-detects common bank layouts), OFX/QFX (SGML and XML variants), QIF. Import is into a chosen account. Preview before commit showing every row's dedup status.

Acceptance:
- Importing the same CSV twice adds zero transactions the second time.
- Importing an OFX with FITID values dedups by FITID; CSV dedups by the fingerprint in 6.5.
- A CSV with a single signed amount column, or separate debit/credit columns, or `Amount` plus a `Type` column, all map correctly.
- Date formats: ISO, US, EU, and "Mon DD, YYYY" are auto-detected; ambiguous cases prompt.

**F-TXN-3 (P0) Bank sync via provider abstraction.** An `IBankDataProvider` interface with operations: `BeginLink`, `CompleteLink`, `ListAccounts`, `SyncTransactions(cursor)`, `GetBalances`, `Unlink`, `GetConnectionHealth`. Two implementations:
- **Plaid** (P0): Link via Hosted Link opened in the system browser; the app polls `/link/token/get` for the public token (no WebView dependency). Uses `/transactions/sync` with stored cursors. Handles `ITEM_LOGIN_REQUIRED` by surfacing a "Reconnect" state and creating an update-mode link token. Bring-your-own-keys model: the user enters their own Plaid client ID and secret in Settings; these are stored in the OS secret store (6.7). Document clearly that shipping a shared production secret in a desktop binary is unsafe and that a hosted proxy is the P2 path to a no-keys experience.
- **SimpleFIN Bridge** (P1): token-based, designed for local apps, no client secret. Setup token → access URL exchange, then `/accounts?start-date=` polling.

Acceptance:
- Sandbox Plaid end-to-end: link, list accounts, initial sync, incremental sync (cursor advances, no duplicates), simulated `ITEM_LOGIN_REQUIRED` shows reconnect state, unlink removes the item and keeps local transactions.
- Sync never blocks the UI; progress and results show in a non-modal status area.
- Balances from the provider are stored as "reported balance" and displayed alongside the ledger balance; a mismatch shows a hint to reconcile.

**F-TXN-4 (P0) Rules engine.** Rules with conditions (payee contains/equals/regex, amount range, account, memo contains, direction) and actions (set payee, set category, set memo, add tag, mark approved, split by fixed amounts or percentages, flag). Rules are ordered; first match wins unless marked "continue". "Create rule from this transaction" prefilled from a selected transaction. Rules can be applied retroactively to existing transactions with a preview.

**F-TXN-5 (P0) Local categorization learner.** When no rule matches, predict category from features: normalized payee tokens, amount bucket, account, day-of-week, direction. Implementation: a naive Bayes or a simple k-NN over the user's approved history, retrained incrementally on each approval. Must be deterministic, explainable ("Suggested because 12 of 13 past 'TRADER JOES' transactions were Groceries"), and never write a category with < 60% confidence (leave uncategorized). Requires ≥ 3 prior examples for a payee before predicting from payee alone.

**F-TXN-6 (P0) Review queue.** A screen listing unapproved transactions across all accounts, oldest first, with the suggested category and confidence. Keyboard triage: `A` approve, `1–9` pick from top suggestions, `C` change category (search-as-you-type), `S` split, `T` mark transfer, `R` create rule, `D` delete, `J/K` move. Batch "approve all with confidence ≥ 90%".

**F-TXN-7 (P0) Search and filters.** Global search (`Ctrl/Cmd+F`) over payee, memo, amount, category, account, tags, with a query syntax (`amount:>100 category:groceries date:2026-08 payee:"trader"`). Saved filters.

**F-TXN-8 (P1) Tags and attachments.** Free-form tags; file attachments (receipt images/PDFs) stored in an `attachments/` folder next to the DB, referenced by hash.

**F-TXN-9 (P1) Payee management.** Merge payees, set default category per payee, rename with retroactive application.

### 5.4 Recurring and bills

**F-REC-1 (P0) Recurring detection.** Nightly (and on import) analysis groups transactions by normalized payee and account and detects periodic patterns (weekly, biweekly, semimonthly, monthly, quarterly, yearly) with amount tolerance (±10% or ±$2, whichever is larger). Emits `RecurringItem` records: payee, cadence, expected amount, next expected date, last seen, confidence, status (active, paused, ended, user-dismissed). The user can confirm, edit, or dismiss detections and manually create recurring items.

**F-REC-2 (P0) Bills and subscriptions view.** Calendar (month) and list views of recurring items with next date, amount, category, and account. Totals: monthly recurring outflow, monthly recurring inflow (paychecks). Filter by subscription vs bill (a recurring item is a "subscription" if the category is in a user-designated subscription group or tagged).

**F-REC-3 (P0) Alerts.** In-app notification center entries for: amount increased > 5% vs previous, expected item missing 3+ days past due, new recurring item detected, first charge after a $0/trial charge from the same payee. Alerts are dismissible and never leave the machine (no email, no push) in v1.

**F-REC-4 (P1) Link recurring items to targets.** One click creates a monthly set-aside target on the item's category equal to the expected monthly amount (annualized for non-monthly cadences), implementing "true expenses".

### 5.5 Goals

**F-GOAL-1 (P0) Goals as targets.** Goals are not a separate data model; a goal is a category with a savings-balance-by-date target and an optional linked tracking account. The Goals screen is a filtered, visual view of such categories with progress rings, projected completion date at current pace, and "what if I added $X/month" slider.

**F-GOAL-2 (P1) Debt payoff planner.** For loan and credit-card accounts with a balance, interest rate, and minimum payment: show payoff date and total interest at current payment; allow "extra per month" and snowball/avalanche ordering across multiple debts; one click sets debt-payment targets accordingly.

### 5.6 Reports and forecast

**F-REP-1 (P0) Spending.** Pie/treemap by category group and category for any date range; drill down to transactions; compare to previous period. Exclude/include tracking accounts and transfers via toggles.

**F-REP-2 (P0) Income vs expense.** Monthly bars over a range with net line; table view exportable to CSV.

**F-REP-3 (P0) Net worth.** Line over time computed from all accounts (assets − liabilities), with per-account stacked area option. Points are monthly end-of-month balances derived from the ledger plus reported balances for investment/tracking accounts that lack transactions (balance snapshots, F-ACC-7 below).

**F-ACC-7 (P0) Balance snapshots.** For tracking accounts (investment, property, loans without transaction feeds) the user or the sync provider records dated balances. Net worth uses the latest snapshot on or before each point.

**F-REP-4 (P0) Cash-flow forecast.** For each on-budget cash account, project daily balance for the next 90 days using: current cleared balance, scheduled transactions, confirmed recurring items (expected date and amount), and optionally the average daily discretionary spend of the last 90 days (toggle). Chart with a shaded "lowest projected balance" marker and a list of the days it dips below a user-set floor.

**F-REP-5 (P1) Age of money and budget health.** Age of money (days between inflow and outflow, YNAB definition), months-ahead metric, percentage of targets funded, overspending count.

**F-REP-6 (P1) Export.** Full export to CSV (transactions, budget, accounts, categories, rules) and to a JSON bundle that can be re-imported into a fresh Keel database losslessly.

### 5.7 Dashboard

**F-DASH-1 (P0) Home dashboard.** Cards: Ready to Assign; accounts overview with balances and sync health; review queue count; upcoming bills (7 days); top overspent/underfunded categories; net worth sparkline; forecast low point. Each card links to its screen. Card layout is fixed in v1 (P2: customizable).

### 5.8 Settings, data, and security

**F-SET-1 (P0) Data file management.** Show the DB path; Change location; Create new budget file; Open existing; Backup now (copies DB and attachments to a timestamped zip); Automatic backups (daily, keep N); Restore from backup with confirmation.

**F-SET-2 (P0) Appearance.** Light, dark, system theme; accent color; density (comfortable/compact); number/date/currency format follows OS locale with override.

**F-SET-3 (P0) Connections.** List of sync connections with institution, accounts, last sync, health, Reconnect, Sync now, Unlink. Provider credentials (Plaid keys, SimpleFIN token) entry stored in the OS secret store.

**F-SET-4 (P1) Optional database encryption.** SQLCipher-encrypted DB with a user passphrase; key cached in the OS secret store per user opt-in. Without opt-in the DB is plain SQLite (documented).

**F-SET-5 (P1) Keyboard shortcut reference and command palette** (`Ctrl/Cmd+K`) listing every action.

### 5.9 Household (P2, design-only in v1)

- Profiles: multiple named people in one ledger; accounts tagged owner (Person A, Person B, Shared); views filtered by owner.
- File-sync collaboration: DB stored in a user-chosen synced folder (Dropbox, iCloud Drive, Syncthing) with a single-writer lock file and conflict detection. No vendor server.
- v1 requirement: the schema includes an `OwnerProfileId` nullable column on accounts and a `Profiles` table with one default row, so this can be added without a destructive migration.

---

## 6. Domain model and budgeting math

This section is normative. Implement it as pure, dependency-free code in `Keel.Domain` with golden-file tests.

### 6.1 Money

- Amounts are stored as `long` **minor units** (cents for USD) in the database and wrapped in a `Money` value object (`Amount: long`, `Currency: string`) in the domain. Never use `double`. `decimal` may be used only at UI boundaries for parsing and formatting.
- Sign convention: **outflows are negative, inflows are positive**, everywhere, for every account type. A credit-card purchase is negative on the card account. A payment to the card is negative on checking and positive on the card.
- Rounding: all arithmetic is integer arithmetic on minor units. Percent splits use banker's rounding with the remainder assigned to the last split so sums are exact.

### 6.2 Entities

| Entity | Key fields | Notes |
|---|---|---|
| `Profile` | Id, Name, IsDefault | One default row in v1 (household P2) |
| `Account` | Id, Name, Type, Currency, IsOnBudget, IsClosed, SortOrder, OpeningDate, Notes, OwnerProfileId?, SyncConnectionId?, ProviderAccountId?, ReportedBalance?, ReportedBalanceAt? | `IsOnBudget` defaults by type (6.3); user can override for Savings/Cash only |
| `SyncConnection` | Id, Provider (Plaid, SimpleFin), InstitutionName, ExternalItemId, Cursor, Status (Ok, NeedsReauth, Error, Disabled), LastSyncAt, LastError, SecretRef | Secrets themselves live in the OS store; `SecretRef` is the key name |
| `CategoryGroup` | Id, Name, SortOrder, IsSystem, IsHidden | System groups: Inflow, Credit Card Payments |
| `Category` | Id, GroupId, Name, SortOrder, IsSystem, IsHidden, LinkedAccountId? (for CC payment categories), Notes, FlexKind (Fixed, NonMonthly, Flex, Unset) | `Ready to Assign` is a system category in Inflow |
| `Transaction` | Id, AccountId, Date, PayeeId?, PayeeRaw, Memo, Amount, CategoryId?, TransferAccountId?, TransferPairId?, Status (Uncleared, Cleared, Reconciled), IsApproved, Source (Manual, File, Provider, System, Scheduled), ProviderTransactionId?, ProviderPendingId?, ImportFingerprint, IsDeleted, CreatedAt, UpdatedAt, ScheduledFromId? | Soft delete; `IsDeleted` rows are excluded from all math |
| `TransactionSplit` | Id, TransactionId, CategoryId?, TransferAccountId?, Memo, Amount | Parent with splits has `CategoryId = null`; sum(splits) == parent amount is a DB check constraint |
| `Payee` | Id, Name, DefaultCategoryId?, IsTransferPayeeForAccountId? | Normalized name unique |
| `BudgetAssignment` | CategoryId, Month (first-of-month date), Assigned | Composite PK; absent row means 0 |
| `Target` | CategoryId, Type, Amount, TargetDate?, Cadence?, LinkedAccountId? | One per category |
| `ScheduledTransaction` | Id, AccountId, Amount, PayeeId, CategoryId?, TransferAccountId?, Memo, RecurrenceRule, NextDate, EndDate?, AutoEnter | Instances become `Transaction` with `ScheduledFromId` |
| `RecurringItem` | Id, PayeeId, AccountId?, Cadence, ExpectedAmount, AmountTolerance, NextExpectedDate, LastSeenDate, Confidence, Status, IsSubscription, CategoryId?, ScheduledTransactionId? | Detection output; user-editable |
| `Rule` | Id, Name, SortOrder, IsEnabled, ContinueAfterMatch, ConditionsJson, ActionsJson | Conditions/actions are versioned JSON |
| `BalanceSnapshot` | AccountId, Date, Balance, Source | For tracking accounts and provider-reported balances |
| `Reconciliation` | Id, AccountId, StatementDate, StatementBalance, CompletedAt | |
| `Tag`, `TransactionTag` | | |
| `Attachment` | Id, TransactionId, FileName, Sha256, MimeType | Files in `<datadir>/attachments/<sha256>` |
| `Alert` | Id, Kind, RecurringItemId?, TransactionId?, CreatedAt, ReadAt?, DismissedAt?, PayloadJson | Notification center |
| `Setting` | Key, ValueJson | App settings that belong with the data file |
| `AuditEvent` | Id, At, Kind, EntityType, EntityId, BeforeJson?, AfterJson? | Powers undo and the transaction activity log |

Indexes required: `Transaction(AccountId, Date)`, `Transaction(Date)`, `Transaction(CategoryId, Date)`, `Transaction(ImportFingerprint)`, `Transaction(ProviderTransactionId)`, `Transaction(PayeeId)`, `BudgetAssignment(Month)`, `BalanceSnapshot(AccountId, Date)`.

### 6.3 Account classification

| Type | On budget by default | Cash-like | Liability |
|---|---|---|---|
| Checking, Savings, Cash | Yes | Yes | No |
| Credit Card, Line of Credit | Yes | No | Yes |
| Loan/Mortgage, Investment, Other Asset, Other Liability | No (tracking) | No | Loan/Other Liability: Yes |

- **On-budget cash accounts** hold money that can be assigned.
- **On-budget credit accounts** participate in the budget through their Credit Card Payment category (6.4.5).
- **Tracking accounts** never affect the budget; transfers to them from on-budget accounts are categorized outflows.

### 6.4 Budget calculation (normative)

Definitions, for a month `M` (first-of-month date) and category `c`:

- `Assigned(c, M)`: the `BudgetAssignment` row, or 0.
- `Activity(c, M)`: sum of `Amount` over all non-deleted transactions (approved or not; splits count individually) dated within `M`, in **on-budget** accounts, with category `c`. Transfers between two on-budget accounts have no category and contribute nothing. Transfers from an on-budget account to a tracking account carry a category and contribute their (negative) amount.
- `CardActivity(c, K, M)`: the portion of `Activity(c, M)` that occurred on credit account `K`.
- `CashActivity(c, M) = Activity(c, M) − Σ_K CardActivity(c, K, M)`.

**6.4.1 Ready to Assign.**

```
InflowRTA(M)     = Σ Amount of transactions in on-budget accounts categorized "Ready to Assign" and dated in M
TotalAssigned(M) = Σ_c Assigned(c, M)   over all non-system-Inflow categories, including Credit Card Payment categories
CashOverspent(c, M) = min(0, RawAvailable(c, M)) − CreditOverspent(c, M)     (both ≤ 0; see 6.4.4)

RTA(M) = Σ_{m ≤ M} InflowRTA(m)
       − Σ_{m ≤ M} TotalAssigned(m)
       − Σ_{m > M} TotalAssigned(m)            ("assigned in future months")
       + Σ_{m < M} Σ_c CashOverspent(c, m)      (prior-month cash overspending reduces RTA; values are ≤ 0)
```

`RTA(M)` may be negative; the UI must show this loudly.

**6.4.2 Available (regular categories).**

```
Carry(c, M)        = max(0, Available(c, M−1))                 (negative balances do not carry; see 6.4.4)
RawAvailable(c, M) = Carry(c, M) + Assigned(c, M) + Activity(c, M)
Available(c, M)    = RawAvailable(c, M)
```

The recursion starts at the earliest month with any assignment or activity for `c`; before that, `Available = 0`.

**6.4.3 Group and month totals.** Group rows sum Assigned, Activity, Available of visible child categories. Month header shows RTA(M), Σ Assigned, Σ Activity, Σ Available.

**6.4.4 Overspending.** If `RawAvailable(c, M) < 0`, the category is overspent by `Overspent(c, M) = RawAvailable(c, M)` (≤ 0). Overspending is split into a credit part and a cash part:

```
CreditSpendingMagnitude(c, M) = Σ_K max(0, −CardActivity(c, K, M))
CreditOverspent(c, M) = −min( −Overspent(c, M), CreditSpendingMagnitude(c, M) )   (≤ 0)
CashOverspent(c, M)   = Overspent(c, M) − CreditOverspent(c, M)                   (≤ 0)
```

Cash overspending reduces next month's RTA (6.4.1). Credit overspending does not; it results in the card's payment category being underfunded (6.4.5). UI colors: cash overspent = red, credit-only overspent = yellow.

**6.4.5 Credit Card Payment categories.** Each on-budget credit account `K` has a system category `Pay_K`. Spending on the card that was covered by budgeted money moves that money into `Pay_K` so it is reserved for the payment.

```
CardSpend(c, K, M) = max(0, −CardActivity(c, K, M))                      (refunds reduce this; floor at 0)
Uncovered(c, M)    = −CreditOverspent(c, M)                               (≥ 0, total uncovered credit spend in c)
Covered(c, K, M)   = CardSpend(c, K, M) − Uncovered(c, M) × CardSpend(c, K, M) / CreditSpendingMagnitude(c, M)
                     (proportional allocation across cards; integer rounding remainder to the largest card)

Payments(K, M)     = Σ Amount of transfers INTO K from on-budget cash accounts dated in M   (> 0)
Refunds are already netted inside CardActivity.

Activity(Pay_K, M)      = Σ_c Covered(c, K, M) − Payments(K, M)
Carry(Pay_K, M)         = max(0, Available(Pay_K, M−1))
Available(Pay_K, M)     = Carry(Pay_K, M) + Assigned(Pay_K, M) + Activity(Pay_K, M)
```

The card's **ledger balance** is independent of this; the budget view shows, next to `Available(Pay_K, M)`, the card balance and the difference ("Available for payment vs. balance"). If `Available(Pay_K)` exceeds the amount owed, the surplus is still shown as available (matches YNAB behavior).

Transfers between two credit accounts, or from a credit account to a cash account (cash advance), are categorized transactions: a cash advance is an inflow to cash categorized as the user chooses (default: Ready to Assign) and an outflow on the card, which flows through `Pay_K` as `Covered` with the same rule.

**6.4.6 Month boundaries and time zones.** Dates are `DateOnly`, stored as ISO `yyyy-MM-dd` text. No time component exists anywhere in the ledger. Month = calendar month in the user's locale-independent civil calendar.

**6.4.7 Worked example (golden test #1).**

Accounts: Checking (cash, on budget), Visa (credit, on budget).
Categories: Groceries, Rent, Pay_Visa.

Month 2026-08:
- 08-01 Checking +$3,000 Ready to Assign (paycheck)
- Assign: Rent $1,500, Groceries $400
- 08-05 Checking −$1,500 Rent
- 08-10 Visa −$250 Groceries
- 08-20 Visa −$200 Groceries
- 08-25 Checking → Visa transfer $100 (payment)

Expected 2026-08:
- Activity(Groceries) = −450; RawAvailable = 0 + 400 − 450 = −50. CreditSpendingMagnitude = 450 ≥ 50, so CreditOverspent = −50, CashOverspent = 0. Groceries shows −50 in yellow.
- Covered(Groceries, Visa) = 450 − 50 = 400. Activity(Pay_Visa) = 400 − 100 = 300. Available(Pay_Visa) = 0 + 0 + 300 = 300. Visa balance = −350. Difference shown: $50 not yet covered.
- Rent: Available = 0.
- RTA(2026-08) = 3,000 − 1,900 = 1,100.

Month 2026-09 (no assignments yet):
- Carry(Groceries) = max(0, −50) = 0. RTA(2026-09) = 3,000 − 1,900 + CashOverspent(<09) = 1,100 (credit overspending does not reduce RTA).
- Carry(Pay_Visa) = 300.

Add to the test: assigning $50 to Groceries in 2026-08 after the fact makes Groceries Available = 0, Covered = 450, Available(Pay_Visa) = 350, RTA = 1,050.

**6.4.8 Golden test #2 (cash overspending).** Checking only. Assign Dining $100 in 08, spend $130 cash in 08. Available(Dining, 08) = −30 red. RTA(09) is reduced by 30 relative to 6.4.1 without the overspending. Carry(Dining, 09) = 0.

**6.4.9 Performance.** `BudgetCalculator.Compute(range)` must compute all categories for a 36-month range over 100k transactions in < 200 ms using pre-aggregated activity (SQL `GROUP BY category, month, account`), then pure in-memory recursion. Never recompute per cell.

### 6.5 Deduplication rules

Applied inside the unified import pipeline, in order:

1. **Provider ID match.** If the incoming record has a provider transaction id (Plaid `transaction_id`, OFX `FITID`, SimpleFIN `id`) and a non-deleted transaction with the same `(SyncConnectionId or AccountId, ProviderTransactionId)` exists → **update in place** (amount, date, payee raw, pending state); do not insert.
2. **Pending-to-posted.** Plaid posted transactions carry `pending_transaction_id`; if a local transaction has `ProviderPendingId` equal to it → update in place and set `ProviderTransactionId`.
3. **Exact fingerprint.** `ImportFingerprint = SHA-256(AccountId | Date | Amount | NormalizedPayee)`; if it exists → **skip** as duplicate.
4. **Fuzzy match.** Same account, same amount, date within ±2 days, Jaro-Winkler(normalizedPayee) ≥ 0.85, and the existing transaction has `Source = Manual` or `Source = Scheduled` → **match**: attach the provider id/fingerprint to the existing transaction (the user entered it first). Show these in the import preview as "matched to existing".
5. Otherwise **insert** as unapproved.

`NormalizedPayee` = uppercase, ASCII-fold, remove digits-only tokens of length ≥ 4, remove tokens in the processor-noise list (`POS`, `DEBIT`, `PURCHASE`, `SQ`, `TST`, `PAYPAL *` prefix, `AMZN MKTP` → `AMAZON`, etc.; list lives in `Keel.Domain/Import/PayeeNormalizer.cs` and is unit-tested), collapse whitespace.

### 6.6 Recurring detection algorithm

For each `(NormalizedPayee, AccountId)` group with ≥ 3 transactions in the last 15 months:

1. Sort by date; compute day gaps.
2. For each candidate cadence (7, 14, 15/16 semimonthly, 30/31 monthly, 91 quarterly, 365 yearly) compute the fraction of gaps within tolerance (weekly ±2 d, biweekly ±3 d, monthly ±5 d, quarterly ±10 d, yearly ±20 d).
3. Pick the cadence with the highest fraction; require ≥ 0.7 and ≥ 3 occurrences (≥ 2 for yearly).
4. Amount: median of the last 6 occurrences; tolerance = max($2, 10%). If the amount varies beyond tolerance but the cadence holds (e.g., utility bills), mark `IsVariableAmount`.
5. `Confidence` = cadence fraction × (1 if amount stable else 0.8).
6. `NextExpectedDate` = last date + cadence, adjusted to the same day-of-month for monthly where possible.
7. Existing `RecurringItem` for the same group is updated, never duplicated; user-dismissed items are not re-created unless the user re-enables detection for that payee.

Price-increase alert: when a new occurrence's amount exceeds the previous by > 5% and > $1.

### 6.7 Secrets and the OS secret store

`ISecretStore { Task<string?> GetAsync(string key); Task SetAsync(string key, string value); Task DeleteAsync(string key); }`

- Windows: DPAPI (`ProtectedData`, `DataProtectionScope.CurrentUser`), blob stored under `%LOCALAPPDATA%\Keel\secrets\`.
- macOS: Keychain via the `Security.framework` (`SecItemAdd/CopyMatching/Delete`) P/Invoke, service name `com.keel.app`.
- Linux: libsecret through D-Bus (org.freedesktop.secrets) when available; fallback to a file encrypted with AES-GCM using a key derived (Argon2id) from `/etc/machine-id` + username, with a visible warning in Settings that the fallback is weaker.

Secrets stored: Plaid client id and secret, Plaid access tokens (one per connection), SimpleFIN access URL, optional SQLCipher key. Never in `appsettings.json`, never in the DB, never logged.

---

## 7. Architecture and technology

### 7.1 Stack (fixed)

| Concern | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 (LTS), C# latest | Single TFM `net10.0` |
| UI | Avalonia 11.3.x (latest stable 11.x at build time), Fluent theme, compiled bindings on | No ReactiveUI |
| MVVM | CommunityToolkit.Mvvm 8.4+ (source generators: `[ObservableProperty]`, `[RelayCommand]`), `WeakReferenceMessenger` | |
| DI / hosting | Microsoft.Extensions.Hosting + DependencyInjection + Logging | Generic host inside the Avalonia app |
| Persistence | EF Core 10 + SQLite (`Microsoft.EntityFrameworkCore.Sqlite`), migrations in `Keel.Infrastructure` | WAL mode, `PRAGMA foreign_keys=ON` |
| Optional encryption | SQLitePCLRaw bundle_e_sqlcipher (P1) | Behind a feature flag |
| Charts | LiveChartsCore.SkiaSharpView.Avalonia (v2) | |
| CSV | CsvHelper | |
| OFX/QIF | Hand-written parsers in `Keel.Infrastructure/Import` | No suitable maintained NuGet |
| Plaid | Going.Plaid (latest) | Already proven in the old codebase |
| HTTP | `IHttpClientFactory` with Polly retries | |
| Logging | Serilog to rolling file in data dir, Debug sink in Debug builds | No PII in logs (payee, amounts are PII) |
| Packaging/updates | Velopack | Windows Setup.exe + MSIX optional, macOS .app in .dmg, Linux AppImage + .deb |
| Tests | xUnit, FluentAssertions, NSubstitute, Avalonia.Headless.XUnit, Verify (snapshot) for golden budget tests | |
| Lint | `dotnet format`, analyzers with `TreatWarningsAsErrors` for `Keel.Domain` and `Keel.Application` | |

### 7.2 Solution layout

```
Keel.sln
Directory.Build.props            (TFM, nullable, analyzers, version)
Directory.Packages.props         (central package management)
src/
  Keel.Domain/                   entities, value objects, BudgetCalculator, PayeeNormalizer,
                                 RecurringDetector, RuleEngine, CategoryLearner, ForecastEngine
                                 (NO package references except System.*)
  Keel.Application/              use-case services + interfaces (IAccountService, IBudgetService,
                                 IImportService, IBankDataProvider, ISecretStore, IBackupService…),
                                 DTOs, validation, messenger message types
  Keel.Infrastructure/           EF Core DbContext, migrations, repositories, SQLite tuning,
                                 importers (Csv/Ofx/Qif), providers (Plaid, SimpleFin),
                                 secret stores (Windows/Mac/Linux), backup, Serilog setup
  Keel.Desktop/                  Avalonia app: App.axaml, Program.cs, Shell, Views/, ViewModels/,
                                 Controls/ (reusable), Converters/, Styles/, Assets/, Services/ (navigation,
                                 dialogs, clipboard, file pickers)
tests/
  Keel.Domain.Tests/             golden budget tests, normalizer, detector, rules, learner
  Keel.Infrastructure.Tests/     importer fixtures (sample CSV/OFX/QIF files), EF in-memory SQLite,
                                 dedup pipeline
  Keel.Desktop.Tests/            headless UI tests for key flows
  Keel.Benchmarks/               BenchmarkDotNet: register load, calculator, import (Section 13)
build/
  ci.yml (GitHub Actions matrix: windows-latest, macos-latest, ubuntu-latest)
  release.yml (Velopack packaging on tag)
docs/
  PRD.md, competitive-analysis.md, decisions/ (ADRs), user-guide/ (later)
```

Dependency direction: `Desktop → Application → Domain`; `Infrastructure → Application → Domain`. `Desktop` references `Infrastructure` only in `Program.cs` for DI registration.

### 7.3 Patterns

- **View-model-first navigation.** `INavigationService.NavigateTo<TViewModel>(params)`; a `ViewLocator` maps `FooViewModel → FooView` by convention. The Shell hosts a sidebar and a content presenter. No routing framework.
- **Async everywhere.** View models call application services, which run on the thread pool and return DTOs. `Dispatcher.UIThread` is only touched in VM base helpers. No EF entities in VMs.
- **Change notification.** Services publish `LedgerChanged(AccountIds, MonthsAffected)` and `BudgetChanged(Months)` through the messenger; open VMs refresh incrementally.
- **Undo.** Every mutating service records an `AuditEvent` with before/after; a global undo stack (Ctrl/Cmd+Z) replays the inverse for the last 50 actions in the session.
- **Money in the UI.** A `MoneyTextBox` control parses locale input, supports `+`/`-`/`*`/`/` inline math (e.g., typing `12.50+3` yields 15.50), and binds to `long` minor units.
- **Virtualization.** Registers use `DataGrid` or a `TreeDataGrid` with `ItemsSourceView` over a paged, DB-backed source; never bind 100k rows to an `ObservableCollection`.
- **Settings split.** App-level settings (theme, window state, last file) in `<appdata>/Keel/settings.json`; data-level settings in the DB `Setting` table.

### 7.4 Data directory

```
<appdata>/Keel/                     Windows: %APPDATA%\Keel   macOS: ~/Library/Application Support/Keel   Linux: ~/.local/share/keel
  settings.json
  logs/
  secrets/                          (Windows DPAPI blobs / Linux fallback only)
  budgets/
    Default.keel                    (SQLite file; extension .keel, registered for double-click open)
    Default.keel-attachments/
    backups/Default-YYYYMMDD-HHMMSS.zip
```

The user may move the `.keel` file anywhere (e.g., a synced folder); the app remembers the path.

### 7.5 Bank data provider interface

```csharp
public interface IBankDataProvider
{
    string ProviderId { get; }                                   // "plaid", "simplefin"
    Task<LinkSession> BeginLinkAsync(LinkMode mode, string? existingConnectionId, CancellationToken ct);
    Task<LinkResult> CompleteLinkAsync(LinkSession session, CancellationToken ct);   // polls or receives callback
    Task<IReadOnlyList<ProviderAccount>> ListAccountsAsync(string connectionId, CancellationToken ct);
    Task<SyncResult> SyncTransactionsAsync(string connectionId, string? cursor, CancellationToken ct);
    Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(string connectionId, CancellationToken ct);
    Task<ConnectionHealth> GetHealthAsync(string connectionId, CancellationToken ct);
    Task UnlinkAsync(string connectionId, CancellationToken ct);
}
```

`SyncResult` = added, modified, removed (provider ids), next cursor, `HasMore`. The application-level `SyncService` loops until `HasMore` is false, maps to the import pipeline, updates `SyncConnection.Cursor` only after the batch commits, and records health.

Plaid specifics: Hosted Link (`hosted_link` in `/link/token/create`, open `hosted_link_url` in the default browser; poll `/link/token/get` every 2 s for up to 10 min for `link_sessions[].results.item_add_results[].public_token`), then `/item/public_token/exchange`. Products: `transactions`. Use `/transactions/sync`. Environment selectable (sandbox/production). Sandbox credentials `user_good` / `pass_good` documented in the user guide for testing.

### 7.6 Repository reset plan (Milestone 0)

Delete everything in the repository **except**: `.gitignore` (replace with a standard .NET + Avalonia + macOS/Linux ignore), `docs/PRD.md`, `docs/competitive-analysis.md`, `docs/PlaidConfiguration.md` (rewrite later as `docs/user-guide/bank-sync.md`), and `LICENSE` if present.

Salvage by reading, not copying: `src/MyApp.Infrastructure/Services/PlaidService.cs` shows working `Going.Plaid` calls for link-token creation, token exchange, and `TransactionsSyncAsync`; reuse the API knowledge, not the code. The `WebView.Avalonia` approach in `PlaidLinkViewModel.cs` is replaced by Hosted Link in the system browser. `AesEncryptionService.cs` with keys in `appsettings.json` is replaced by the OS secret store; do not carry it forward.

Rewrite `CLAUDE.md` at M0 to describe the new solution, build/test commands, and the conventions in Section 15. Rewrite `README.md`.

---

## 8. Cross-platform requirements

- **Targets:** Windows 10 1809+ (x64, arm64), macOS 13+ (x64, arm64 universal), Ubuntu 22.04+ / Fedora 39+ (x64; arm64 best-effort). Wayland and X11.
- **Keyboard:** use Avalonia's `PlatformHotkeyConfiguration` so `Cmd` is the modifier on macOS and `Ctrl` elsewhere; show the correct glyph in menus and tooltips.
- **Native menus:** `NativeMenu` on macOS (app menu with About/Preferences/Quit), in-window menu bar on Windows/Linux.
- **Fonts:** bundle Inter (`Avalonia.Fonts.Inter`) as the UI font; monospace tabular figures for amounts (`font-variant-numeric: tabular-nums` equivalent via `FontFeatures`).
- **HiDPI:** all assets vector (SVG via `Avalonia.Svg.Skia`) or multi-resolution.
- **File pickers, clipboard, open-in-browser:** through Avalonia storage/platform APIs only; no `Process.Start` on URLs without the platform launcher.
- **Window state:** size, position, maximized, sidebar width persisted per display configuration.
- **Single instance:** second launch focuses the running instance and, if given a `.keel` path, opens it.
- **Startup:** < 2 s to interactive with the default file (measured in CI on the ubuntu runner with a 100k-transaction fixture).

---

## 9. Screens and UX specification

### 9.1 Shell

- **Left sidebar** (collapsible to icons): Home, Budget, Review (badge = unapproved count), Bills, Goals, Reports, then an **Accounts** section listing on-budget accounts (grouped: Cash, Credit) and Tracking accounts with balances, then Settings at the bottom. Account rows show a small health dot when linked (green ok, amber needs attention, red error) and a pending-sync spinner.
- **Top bar:** current file name, global search box (`Ctrl/Cmd+F`), "Sync all" button with last-sync time, notification bell (alerts), undo/redo.
- **Status strip** (bottom, non-modal): import/sync progress messages and result toasts with an "Undo" action where applicable.
- **Command palette** (`Ctrl/Cmd+K`): fuzzy list of every action and navigation target.

### 9.2 Home (F-DASH-1)

Two-column responsive card grid. Cards in order: Ready to Assign (large number, "Assign" button opens Budget), Review queue (count, "Start review"), Accounts (balances, health), Upcoming bills 7 days (list), Budget alerts (overspent/underfunded, top 5), Forecast (sparkline of lowest-balance account, low point), Net worth (sparkline 12 months). Empty states explain the next step (e.g., "Add an account to begin").

### 9.3 Budget (F-BUD-*)

- Header: month picker (`Alt+←/→`), Ready to Assign pill (green ≥ 0, red < 0), quick actions: Fund targets, Undo.
- Grid: group rows (collapsible, with totals) and category rows: Name (with target badge), Assigned (editable `MoneyTextBox`, tab moves down the column), Activity (click opens filtered transactions), Available (pill colored: green > 0, gray = 0, yellow credit-overspent, red cash-overspent; drag pill to another row to move money).
- Right inspector panel (toggle `I`): selected category's target editor, quick-assign buttons, notes, 6-month history sparkline, and "How is Available computed?" breakdown listing carry, assigned, activity, and per-card covered amounts.
- Keyboard: arrow navigation between cells, `Enter` edit, `M` move money dialog, `T` set target, `Ctrl/Cmd+Shift+F` fund targets.
- P1: three-month view; Flex view toggle.

### 9.4 Account register (F-ACC-2..5)

- Header: account name, type, ledger balance, cleared balance, uncleared balance, reported balance (if linked) with a mismatch hint, buttons: Add transaction, Import file, Reconcile, Sync (if linked), Edit account.
- Filter bar: date range presets, search, status filter, category filter, "unapproved only".
- Grid columns as in F-ACC-2; unapproved rows have a left accent bar; scheduled ghost rows italic; transfers show the paired account as payee with a transfer icon; splits expand inline.
- Add/edit row appears inline at the top; payee field autocompletes and, on selecting a known payee, pre-fills default category and last memo.
- "All Accounts" register adds an Account column.

### 9.5 Review (F-TXN-6)

Single-focus list: one transaction highlighted at a time with its details and up to 5 suggested categories (rule match first, then learner suggestions with confidence and the explanation string). Progress bar "23 of 61". Batch approve control. Keyboard map shown in a footer.

### 9.6 Bills (F-REC-*)

Tabs: Calendar (month grid with amounts on days), List (sortable: next date, payee, amount, cadence, category, account, status), Subscriptions (filtered list with monthly and yearly totals and price-change history). Detail panel: history chart of amounts, "create target" (P1), pause/dismiss/edit. Alerts for this item.

### 9.7 Goals (F-GOAL-*)

Card per goal category: name, progress ring (available / target), monthly need, projected completion, "what if" slider. "New goal" opens a wizard: name, amount, date, optional linked account, then creates category + target. P1: Debt payoff tab.

### 9.8 Reports (F-REP-*)

Left list of reports; each report has a shared toolbar (date range, accounts filter, include transfers/tracking toggles, export CSV/PNG). Charts use one consistent categorical palette defined in `Styles/Charts.axaml`, with light and dark variants and no color as the sole signal. Every chart element is clickable to the underlying transactions.

### 9.9 Settings

Sections: General (file, backups, locale), Appearance, Connections, Categories (manage groups/categories/hidden), Payees, Rules, Import mappings, Shortcuts, Privacy & Stats, About.

### 9.10 First-run experience

1. Welcome: "Create a new budget file" or "Open existing" (and "Import from YNAB/Monarch export" as P1: CSV importers for their export formats).
2. Choose a starter template (or empty).
3. Add first account (manual with balance) or connect a bank (requires keys; explain why, link to guide).
4. Land on Budget with Ready to Assign showing the opening balance and a 4-step checklist card on Home.

Total time target: under 15 minutes to first fully assigned month.

### 9.11 Visual design

- Fluent theme base, custom accent, 8-px spacing grid, 13-px base font, tabular numerals for all amounts, right-aligned amounts, negative in red only where semantically negative (outflows in registers are plain; overspending is red).
- Light and dark themes, both tested for contrast (WCAG AA for text).
- Motion: subtle, ≤ 150 ms, respects OS "reduce motion".
- Empty, loading, and error states designed for every screen.

---

## 10. Security and privacy

- **No network calls** except: the configured bank provider(s) when the user syncs, and the Velopack update check (opt-out in Settings, off by default in v1 until a release feed exists).
- **No telemetry, ever.** Local stats only (Section 4).
- **Secrets** only in the OS secret store (6.7). Secret values are never rendered after entry (show "••••" and a "Replace" button).
- **Logs** redact payee names and amounts; log level Info by default; a "Copy diagnostic bundle" action in Settings creates a zip with logs and a schema-only DB summary (no rows).
- **Plaid keys warning:** Settings and the user guide state plainly that a shared production secret must not be embedded in a distributed binary; each user uses their own developer keys, or the project provides a hosted token-exchange proxy (P2).
- **File integrity:** the DB runs `PRAGMA integrity_check` on open once per day; backups are verified by reopening the copy.
- **Encryption at rest:** optional SQLCipher (P1). Attachments are not encrypted in v1 (documented).
- **Dependency hygiene:** central package management, `dotnet list package --vulnerable` in CI, Dependabot.

---

## 11. Non-functional requirements

| Area | Requirement |
|---|---|
| Performance | Register open < 500 ms at 100k rows; budget month switch < 100 ms; import 10k rows < 5 s; app cold start < 2 s |
| Reliability | All writes in transactions; crash-safe (WAL); auto-backup before every migration; undo for every user action |
| Offline | 100% functional without network except sync |
| Accessibility | Full keyboard operation; screen-reader names on all controls (Avalonia automation peers); 200% scaling; color never the only signal (icons/text accompany red/yellow) |
| Localization | English (US) strings in `.resx` from day one; number/date/currency formatting via `CultureInfo`; RTL not required in v1 |
| Data safety | Never delete user data without confirmation + undo; migrations tested forward from every released schema version |
| Footprint | Installer < 80 MB per platform; idle memory < 250 MB with 100k transactions |
| Compatibility | The `.keel` file format is versioned; older app versions refuse newer files with a clear message |

---

## 12. Milestones and build order

Each milestone ends with: green CI on all three OSes, tests for its scope, a runnable app, an updated `CHANGELOG.md`, and a short ADR for any deviation from this PRD. Do not start a milestone before the previous one meets its exit criteria.

**M0. Reset and skeleton (P0).**
Reset repo per 7.6. Create solution layout (7.2), `Directory.Build.props`, central packages, CI matrix, Serilog, DI host, Shell with sidebar and empty screens, theme switching, settings.json, data directory, EF Core DbContext with full schema from 6.2 and initial migration, `Money` type, headless UI smoke test.
Exit: app launches on all OSes to an empty Home; `dotnet test` green.

**M1. Ledger (P0).**
F-ACC-1..5, F-ACC-7 (snapshots), payees, `MoneyTextBox`, register virtualization with 100k fixture generator, search (F-TXN-7 basic), undo, audit events.
Exit: manual bookkeeping is fully usable; performance targets met on the fixture.

**M2. Budget engine (P0).**
`BudgetCalculator` with golden tests 6.4.7/6.4.8 plus edge cases (refunds on cards, transfers to tracking, closed accounts, mid-month opening, negative RTA, future assignments). Budget screen (9.3), move money, targets (F-BUD-4), quick assign (F-BUD-5), templates (F-BUD-8 can slip to P1).
Exit: an envelope budget can be run end-to-end for three months of fixture data with numbers matching the golden files.

**M3. File import (P0).**
Unified pipeline (F-TXN-1), dedup (6.5), CSV mapping dialog, OFX/QFX, QIF, import preview, transfer detection.
Exit: fixture files from 5 real-world bank layouts (anonymized) import correctly and idempotently.

**M4. Rules, learner, review (P0).**
F-TXN-4, F-TXN-5, F-TXN-6, payee normalizer, "create rule from transaction", retroactive apply with preview.
Exit: learner accuracy test on a labeled fixture ≥ 85% after 200 approvals; review keyboard flow headless-tested.

**M5. Recurring, alerts, forecast, scheduled (P0 + F-ACC-6).**
F-REC-1..3, F-ACC-6 scheduled transactions, F-REP-4 forecast engine, notification center.
Exit: detector tests on synthetic and fixture series; forecast golden test.

**M6. Reports, goals, dashboard (P0).**
F-REP-1..3, F-GOAL-1, F-DASH-1, chart styling, export CSV.
Exit: every chart drills to transactions; dashboard cards live.

**M7. Bank sync (P0 Plaid, P1 SimpleFIN).**
`IBankDataProvider`, OS secret stores, Connections settings, Plaid Hosted Link flow, `/transactions/sync` with cursors, health states, reconnect, unlink, provider balances → snapshots. Sandbox integration tests behind an env-var gate.
Exit: sandbox end-to-end per F-TXN-3 acceptance on all OSes (secret store per OS verified).

**M8. Polish and packaging (P0).**
First-run experience, empty states, accessibility pass, keyboard reference, backups/restore, integrity check, Velopack packaging for three OSes, user guide (`docs/user-guide/`), CLAUDE.md and README final.
Exit: v1.0.0 tag builds installers in CI; a fresh user can complete Section 9.10 in under 15 minutes.

**M9. P1 backlog** (order by value): SimpleFIN, Flex mode, debt payoff planner, three-month budget view, tags and attachments, payee merge, budget health metrics, JSON full export/import, YNAB/Monarch CSV importers, SQLCipher, command palette completeness.

---

## 13. Testing and quality gates

- **Domain:** ≥ 90% line coverage. Golden tests for the budget calculator are the highest-value tests in the repo; add a golden case for every bug fixed in budget math.
- **Importers:** fixture-driven; each fixture has an expected-output JSON. Add a fixture per bank layout encountered.
- **Dedup:** property-based test (FsCheck or CsCheck) that importing any batch twice yields no new rows.
- **Learner/detector:** accuracy thresholds asserted on labeled fixtures; deterministic seeds.
- **UI:** Avalonia.Headless tests for: add transaction via keyboard, categorize in review, assign in budget and see RTA change, import preview flow, theme switch. Screenshot snapshot tests for Home and Budget in light and dark (compare with tolerance).
- **Performance:** a `Keel.Benchmarks` (BenchmarkDotNet) project with register load, calculator, and import benchmarks; CI fails if calculator regresses > 25% against the stored baseline.
- **CI matrix:** build + test on windows-latest, macos-latest, ubuntu-latest; `dotnet format --verify-no-changes`; vulnerable package scan.
- **Manual QA checklist** in `docs/qa-checklist.md`, run before each tag.

---

## 14. Decisions made under assumption (owner may override)

| # | Decision | Assumption / alternative |
|---|---|---|
| D1 | Working name "Keel" | Any name; single find-and-replace plus icon |
| D2 | Bring-your-own Plaid keys in v1 | Owner has or will create a Plaid developer account; the alternative (hosted proxy) needs infrastructure and is P2 |
| D3 | Single base currency per file | Multi-currency budgeting is a large feature; accounts still record currency for the future |
| D4 | No cloud, no accounts, no sync service | The whole thesis; household via synced folder is P2 |
| D5 | CommunityToolkit.Mvvm instead of ReactiveUI | Simpler for generated code and for an agent; old repo mixed both |
| D6 | EF Core over Dapper/raw SQLite | Migrations and productivity; hot paths use raw SQL views |
| D7 | Investment accounts are balance-only in v1 | Holdings tracking is P2; Monarch/Copilot depth is not a v1 goal |
| D8 | Learner is naive Bayes / k-NN, not an LLM | Local, explainable, no network; LLM assistant is an opt-in P2 |
| D9 | Business model unspecified | Nothing in the code depends on it; no license checks, no tiers |
| D10 | Desktop only; no mobile in v1 | Architecture keeps Domain/Application UI-agnostic for a later Avalonia mobile head |
| D11 | Velopack for packaging | Alternatives: platform-native scripts; Velopack covers all three OSes and updates |
| D12 | `.keel` file extension registered | Cosmetic; can be dropped |

Open questions for the owner (not blocking; defaults above apply):
1. Will you host a Plaid token-exchange proxy, or is BYO-keys acceptable long-term?
2. Do you want YNAB/Monarch migration importers in v1 (P1 as written)?
3. Is Linux a hard requirement or best-effort? (Written as hard.)
4. Any preference on app name and license (MIT assumed for the repo)?

---

## 15. Execution guidance for the implementing agent

1. **Read this PRD and `docs/competitive-analysis.md` fully before M0.** Keep a `docs/decisions/NNNN-title.md` ADR for every deviation, with the reason.
2. **Work milestone by milestone** (Section 12). Within a milestone, work vertically: domain → application → infrastructure → UI → tests, feature by feature, keeping the app runnable after every commit.
3. **Commit small and often** with conventional-commit messages (`feat(budget): …`, `fix(import): …`, `test(domain): …`). Push at least at every milestone exit.
4. **Tests first for Section 6.** Write the golden tests from 6.4.7 and 6.4.8 before the calculator; they are the spec.
5. **No placeholder UI.** Every screen listed in Section 9 that is in scope for the current milestone must be functional, with empty/loading/error states. Do not leave `TODO` views in the sidebar.
6. **Cross-platform from day one.** Never use Windows-only APIs outside `Keel.Infrastructure/Platform/Windows`. CI must stay green on all three runners; a platform-specific failure is a blocker, not a follow-up.
7. **Performance fixtures early.** Build the 100k-transaction generator in M1 and run the register against it before adding features.
8. **Accessibility and keyboard are features**, not polish. Add automation names and shortcuts as each control is built.
9. **Do not add features not in this PRD** without an ADR. Do not remove or weaken acceptance criteria. If something is infeasible, write the ADR, implement the closest feasible behavior, and note it in the milestone summary.
10. **Keep `CLAUDE.md` current** with build/test/run commands, the solution map, and the conventions above, so later sessions start productive.
11. **Definition of done** is in Section 0. A milestone is not done until its exit criteria are demonstrated, with the commands and results recorded in `CHANGELOG.md`.
