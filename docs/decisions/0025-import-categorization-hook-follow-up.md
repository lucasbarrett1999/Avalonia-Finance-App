# 25. Import categorization: adapter now, hook wiring later

- Status: Accepted
- Date: 2026-09-24
- Milestone: M4

## Context

F-TXN-1 step 4 runs rules, then the learner, on every imported batch. The import pipeline is being
built in parallel and will call categorization through a seam named `IImportCategorizationHook`.
When this work started, that interface was not on `main`, so this branch cannot implement it
without inventing (and then conflicting with) the import team's contract.

## Decision

- `Keel.Infrastructure.Categorization.ImportCategorizationAdapter` exposes the seam's method shape,
  `Task ApplyAsync(IReadOnlyList<Guid> transactionIds, CancellationToken ct)`, and delegates to
  `ICategorizationService.CategorizeAsync`: stored rules write their mutations, then the payee
  default (0.95) or the learner's primary suggestion (at least 0.60) is written for rows that are
  still uncategorized (ADR 0022). Rows stay unapproved unless a rule marks them approved. It runs
  as one undoable `ApplyRules` action.
- The adapter is registered as a singleton in `AddKeelInfrastructure`.

## Consequences

- Follow-up when the import branch lands: add `: IImportCategorizationHook` to the adapter and one
  DI line, `services.AddSingleton<IImportCategorizationHook>(sp => sp.GetRequiredService<ImportCategorizationAdapter>());`.
- The pipeline should call the hook after inserting a batch and before publishing its summary, and
  must not call it inside its own `LedgerWriter` unit of work (the adapter opens its own; the writer
  is not re-entrant). Loading the learner model reads on its own connection first.
