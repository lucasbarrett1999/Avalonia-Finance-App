# 1. Record architecture decisions

- Status: Accepted
- Date: 2026-09-24
- Milestone: M0

## Context

`docs/PRD.md` fixes the architecture and stack (sections 6 to 8) and says that any deviation
must be recorded with its reason (sections 0, 12 and 15). Later sessions, possibly by other
agents, need to know why the code differs from the PRD without re-deriving it.

## Decision

We keep lightweight Architecture Decision Records in `docs/decisions/`, one Markdown file per
decision, named `NNNN-short-title.md` with a four-digit sequence number. Each record has:

- a title, status (Proposed, Accepted, Superseded by NNNN), date and milestone;
- **Context**: what the PRD says and why it does not fit as written;
- **Decision**: what we do instead;
- **Consequences**: what follows, including follow-up work.

An ADR is required for every deviation from the PRD, for every library substitution, and for
any feature added that the PRD does not list. Records are never deleted; a changed decision
gets a new record that supersedes the old one.

## Consequences

- Deviations are visible in one place and linked from `CHANGELOG.md` milestone entries.
- The cost is a short file per decision, which is small next to the cost of an unexplained one.
