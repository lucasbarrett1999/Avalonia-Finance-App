# 2. CI workflow lives in .github/workflows, not build/

- Status: Accepted
- Date: 2026-09-24
- Milestone: M0

## Context

PRD 7.2 lists the CI definition as `build/ci.yml` (and later `build/release.yml`). GitHub
Actions only runs workflow files from `.github/workflows/`; a file under `build/` would never
execute, so the "green CI on all three OSes" exit criterion of every milestone could not be met.

## Decision

The build-and-test matrix (windows-latest, macos-latest, ubuntu-latest), `dotnet format
--verify-no-changes`, and the vulnerable-package scan live in `.github/workflows/ci.yml`.
The Velopack release workflow (M8) will be `.github/workflows/release.yml`. The `build/`
folder is reserved for scripts those workflows call, if any are needed.

## Consequences

- CI runs on every push and pull request with no extra wiring.
- Anyone following PRD 7.2 literally should look in `.github/workflows/`.
