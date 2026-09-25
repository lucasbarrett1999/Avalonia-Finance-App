# 84. First-run setup and the Home checklist

- Status: Accepted
- Date: 2026-09-25
- Milestone: M8

## Context

PRD 9.10 (normative): Welcome (create a new budget file or open an existing one), a starter template
(or empty), the first account (manual with balance) or connect a bank (requires keys; explain why, link
to the guide), then Budget with Ready to Assign showing the opening balance and a 4-step checklist on
Home. Earlier milestones created `budgets/Default.keel` silently on first launch.

## Decision

- **When it shows**: only when settings.json has not recorded a finished setup, no file is remembered
  and `Default.keel` does not exist. Then no file is created until the user chooses. Any successful
  open of an existing file, or finishing or skipping the account step, sets `firstRunCompleted`; the
  setup never shows again. Returning users (and every existing test host) never see it.
- **Steps**: 1. Welcome, with the new file's name and folder (default `budgets/My Budget.keel`, a
  save picker to choose another place) or Open existing (open picker). Creating continues in the new
  file's session (ADR 0080) at step 2. 2. The M2 starter templates plus "Start empty". 3. Name, type
  (checking, savings, cash, credit card), balance or amount owed, and the budget currency (from the OS
  region); "Skip for now"; and a "How bank connections work" panel explaining the own-keys
  requirement, with buttons to open the bank sync guide and Settings → Connections (which ends the
  setup). The YNAB/Monarch importers (P1) are not offered.
- **Landing**: adding the account goes to Budget, where Ready to Assign equals the opening balance
  (the account's starting balance is Inflow: Ready to Assign, PRD 6.4).
- **Checklist**: "Get started" on Home with four steps (budget file, categories, account, assign
  money). Each step is computed from the file (`ISetupProgressService`: a user category exists, an
  account exists, a non-zero assignment exists), so it is right whichever way the user did it; the
  file step is always done. The card can be hidden per file (`setup.checklistDismissed` in the
  `Setting` table). Done steps show a check mark and the word "Done", never colour alone.
- Creating another file later from Settings → General resumes the setup at step 2 in that file.

## Consequences

- The 15-minute target (G1, PRD 9.10) is a human measure; the headless test completes the flow with
  its fixed inputs, and the QA checklist times a real first run.
