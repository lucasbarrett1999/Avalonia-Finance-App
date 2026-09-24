# Keel

Keel is a native, cross-platform desktop app for personal finance. It combines zero-based
envelope budgeting (in the style of YNAB) with a full financial picture (net worth, recurring
bills, cash-flow forecast), and keeps all of your data in a single SQLite file on your own
computer. It works offline and needs no account. Bank sync is optional; file import and manual
entry are first-class.

Built with .NET 10 and Avalonia 11 for Windows 10+, macOS 13+ and Linux (Ubuntu 22.04+).

The product spec is [`docs/PRD.md`](docs/PRD.md); the market research behind it is
[`docs/competitive-analysis.md`](docs/competitive-analysis.md).

## Status

Pre-release. **Milestone 0 (reset and skeleton) is complete**: the solution layout, the domain
model and database schema, the application shell with every screen's empty state,
light/dark/system themes, settings and logging, and CI on all three operating systems. Nothing
can be recorded yet: accounts and transactions arrive in M1, the budget engine in M2. See
[`CHANGELOG.md`](CHANGELOG.md) and the milestone plan in PRD section 12.

## Build and run

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download). `global.json` pins the major
version.

```bash
dotnet restore Keel.sln
dotnet build Keel.sln
dotnet test Keel.sln
dotnet run --project src/Keel.Desktop
```

On first launch Keel creates its data folder and an empty budget file:

| OS | Data folder |
|---|---|
| Windows | `%APPDATA%\Keel` |
| macOS | `~/Library/Application Support/Keel` |
| Linux | `~/.local/share/keel` (or `$XDG_DATA_HOME/keel`) |

Inside it: `settings.json` (theme, window size, last file), `logs/`, and
`budgets/Default.keel`, the SQLite database that holds your budget.

## Repository layout

| Path | What it is |
|---|---|
| `src/Keel.Domain` | Money, entities, account rules, and (from M2) the budget math. No dependencies. |
| `src/Keel.Application` | Service interfaces, DTOs, messages. |
| `src/Keel.Infrastructure` | EF Core + SQLite, migrations, data folder, settings, logging. |
| `src/Keel.Desktop` | The Avalonia app. |
| `tests/` | Unit, database and headless UI tests; benchmarks. |
| `docs/decisions/` | Architecture decision records. |

Contributor and agent notes, including conventions, are in [`CLAUDE.md`](CLAUDE.md).

## Privacy

Keel sends no telemetry. Your data never leaves your computer unless you connect a bank, and
then only to the provider you configure.

## License

Not chosen yet; the PRD assumes MIT (PRD 14, open question 4).
