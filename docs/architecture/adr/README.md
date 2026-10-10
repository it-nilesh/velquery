# Architecture Decision Records

Short records of the decisions that shape VelQuery. These are the PRD §27 kickoff ADRs. Where the PRD sketch and the
code disagree, the ADR follows **what the code implements** and explains why the PRD sketch was not used.

Status values: `Proposed` (awaiting review at Gate A), `Accepted`, `Superseded by ADR-xxxx`, `Rejected`.
An accepted ADR is not edited in substance; a new ADR supersedes it.

## Index

| ADR | Title | Status | Date |
|---|---|---|---|
| [ADR-0001](ADR-0001-query-first-scope-and-dependency-isolation.md) | Query-first scope and dependency isolation | Proposed | 2026-10-10 |
| [ADR-0002](ADR-0002-session-connection-transaction-ownership.md) | Session, connection and transaction ownership; streaming disposal; error contract | Proposed | 2026-10-10 |
| [ADR-0003](ADR-0003-generated-contracts-vs-dynamic-sql.md) | Generated typed query contracts vs runtime SQL | Proposed | 2026-10-10 |
| [ADR-0004](ADR-0004-mapping-semantics.md) | Mapping semantics | Proposed | 2026-10-10 |
| [ADR-0005](ADR-0005-parameter-metadata-and-providers.md) | Parameter metadata and provider adapters | Proposed | 2026-10-10 |

Still to be written (PRD §27): ADR-0006 static SQL subset and schema snapshots, ADR-0007 Native AOT provider/auth
matrix, ADR-0008 benchmark fairness and publishing of losses, ADR-0009 diagnostics privacy and backpressure,
ADR-0010 no implicit retry / partial streaming, ADR-0011 AI tool permissions, ADR-0012 versioning and packaging.

## Template

Copy this into `ADR-NNNN-short-title.md`. Keep it to one or two pages; link to code and PRD sections instead of
repeating them.

```markdown
# ADR-NNNN: Title

- **Status:** Proposed
- **Date:** YYYY-MM-DD
- **Deciders:** (roles, see PRD §23)
- **PRD references:** VQ-FR-xxx, §x.y
- **Code:** paths to the types that implement the decision

## Context

What forces are at play: requirements, constraints, what the PRD sketch says, what we learned while implementing.

## Decision

What we do, stated precisely enough to test. Use "must"/"never" only for rules that are enforced.

## Consequences

What becomes easier or harder, including costs we accept and follow-up work.

## Alternatives considered

Each option with the reason it was not chosen.

## Open questions

Items that need a decision before the gate named, with an owner if known.
```
