# Getting started

## Install

Download the package for your system from the project's
[releases page](https://github.com/lucasbarrett1999/Avalonia-Finance-App/releases):

| System | File | Notes |
|---|---|---|
| Windows 10 or 11 (x64, Arm) | `Keel-win-x64-Setup.exe` or `Keel-win-arm64-Setup.exe` | Installs for your user only; no administrator rights. `.keel` files open in Keel. |
| macOS 13+ (Apple silicon, Intel) | `Keel-<version>-osx-arm64.dmg` or `...-osx-x64.dmg` | Drag Keel to Applications. A `.pkg` installer is also provided. |
| Ubuntu 22.04+, Debian 12+ | `keel_<version>_amd64.deb` | `sudo apt install ./keel_<version>_amd64.deb`, then start Keel from the app menu or run `keel`. |
| Other Linux (x64) | `Keel-linux-x64.AppImage` | `chmod +x Keel-linux-x64.AppImage` and run it. |

Release candidates may be unsigned. Windows SmartScreen then says "Windows protected your PC":
choose **More info → Run anyway**. On macOS, right-click Keel in Applications and choose **Open**
the first time.

## The first-run setup

The first time Keel starts it walks you through four steps. Nothing is created until you choose.

1. **Welcome.** Create a new budget file (name it, and optionally choose another folder, for
   example a synced folder), or **Open existing** if you already have a `.keel` file. Two more ways
   in: **Restore from a Keel export bundle…** creates the file from a bundle made with Export
   ([Backups and the data file](backups-and-data.md#export-and-import)), and **Import from YNAB or
   Monarch export…** creates the file and brings in your accounts, categories and history from the
   other app ([Importing files](importing.md#moving-to-keel-from-ynab-or-monarch)); both skip the
   remaining steps.
2. **Starter categories.** Pick a template (Simple, Detailed, Student, Family) or **Start empty**.
   You can rename, add, hide and reorder everything later (Budget → Manage categories).
3. **Your first account.** Enter the account name, type (checking, savings, cash or credit card),
   today's balance (for a card, the amount you owe) and the currency. **Skip for now** is fine too.
   To use bank sync instead, open **How bank connections work**: sync needs your own provider keys,
   and the buttons open the [bank sync guide](bank-sync.md) and Settings → Connections.
4. **Budget.** Keel opens the Budget with your balance in **Ready to Assign**. Give every dollar a
   job by typing amounts in the Assigned column (see [Budgeting in Keel](budgeting.md)).

Home shows a **Get started** card with the four steps (budget file, categories, account, assign
money). Steps tick themselves off as you do them, whichever way you do them; **Hide** removes the
card for this file.

## Adding more accounts

**Add account** at the bottom of the sidebar's Accounts list. On-budget accounts (checking, savings,
cash, credit cards) take part in the budget; tracking accounts (investments, property, loans) count
toward net worth only. A new account's starting balance becomes Ready to Assign (cash) or a card
balance to pay off (credit).

## Finding your way

- The **sidebar**: Home, Budget, Review, Bills, Goals, Reports, your accounts, Settings.
  `Ctrl+1`…`Ctrl+7` jump to the pages; `Ctrl+B` collapses the sidebar.
- **`Ctrl+K`** opens the command palette: type part of any action or screen name ("bdg", "backup").
- The **menu bar** (File, Edit, View, Go, Help; the macOS menu bar on a Mac) has the same actions.
- **`Ctrl+F`** searches transactions from anywhere.
- **Undo** (`Ctrl+Z`) works for every change to your data, including imports and budget changes.
- The **status strip** at the bottom reports what just happened, with **Undo** where it applies.

## Appearance and formats

Settings → Appearance: light, dark or system theme; accent color; comfortable or compact density;
motion (follow the system, reduce, or full); and number, date and currency formats (follow the system
or choose a region). Changing the format reopens the budget file so every screen uses it.
