# Transactions, tags and attachments

Every account has a register (click it in the sidebar); **All accounts** shows every account in one list.

## The register

- `N` adds a transaction in the editor row at the top; `Enter` edits the selected row. In the editor,
  `Enter` saves, `Ctrl+Enter` saves and starts another, `Esc` cancels.
- `C` toggles cleared, `A` approves, `Delete` deletes (with undo), `T` edits the tags of the selected row.
- Select several rows (Shift or Ctrl + click) to categorize, approve, clear, move or delete them together.
- Right-click a row for **Create rule from this transaction…**.

## Search

Type in the register's search box, or press `Ctrl+F` anywhere to search all accounts. Plain words match
the payee, memo, category, account and tag names. Keys narrow the search further:

| Search | Finds |
|---|---|
| `payee:"trader joe"` | payee contains the text |
| `memo:refund` | memo contains the text |
| `category:groceries` | category (or a split line's) contains the text |
| `account:visa` | account name contains the text |
| `tag:trip` | a tag contains the text |
| `has:tag` | transactions with any tag |
| `has:attachment` | transactions with an attached file |
| `amount:>100`, `amount:10..20` | amount (either direction) above, below or between |
| `date:2026-08`, `date:2026-08-01..2026-08-15` | a year, month, day or range |

The filter bar next to the search box filters by dates, status, category and (once the file has tags) tag.
In a narrow window the filters sit under the search box.

## Tags

Tags are free-form labels such as "Trip 2026", "Tax" or "Reimbursable". A transaction can have any number.

- **Add tags** in the editor's Tags box, under the other fields (press `T` on a row to go straight there).
  Type a tag and press `Enter`; suggestions come from the tags you already have, and an existing tag keeps
  its spelling whatever case you type. `Backspace` in the empty box removes the last tag; the × on a chip
  removes that one. New tags are created when you save, and one undo removes them again.
- **See tags** as chips before the memo in the register.
- **Find tagged transactions** with the tag filter, `tag:name` or `has:tag`.
- **Manage tags** in **Settings → Tags**: each tag shows how many transactions and rules use it (click the
  count to see the transactions). **Rename** changes the tag everywhere, and rules that add or look for it
  follow. **Merge** moves a tag's transactions onto another tag and removes it. **Delete** removes the tag
  from every transaction after a confirmation. Everything can be undone.
- **Flagged** is the tag rules use for their "flag" action. You can add or remove it on a transaction and
  delete it to unflag everything, but not rename or merge it.
- A tag can mark subscriptions: see **Settings → Bills and subscriptions** ([Bills](bills-and-scheduling.md)).

## Attachments

Attach receipts, invoices or any other file to a transaction.

- In the editor, **Attach file…** picks one or more files, or drop files anywhere on the editor. On a new
  transaction the files are attached when you save.
- Click an attachment to open it with its usual app (Keel opens a read-only copy), or × to remove it.
  Attaching and removing can be undone.
- A paperclip with a count shows on register rows with attachments; search `has:attachment` to list them.
- Keel copies attached files into the `Name.keel-attachments` folder next to the budget file, named by
  their content (a SHA-256 hash), so the same file is stored once. Backups include the folder, restore
  puts it back, and **Move budget file** moves it. Files nothing refers to any more are deleted by the
  daily maintenance about a month after they were last removed. Attachments are **not encrypted**; see
  [Backups and the data file](backups-and-data.md).
