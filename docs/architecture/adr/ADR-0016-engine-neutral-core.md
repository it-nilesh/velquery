# ADR-0016: Engine-neutral core (phase 2)

- **Status:** Proposed
- **Date:** 2026-10-20
- **Implements:** ADR-0015 decision 3; PRD amendment v1.1 phase 2 (EP-19)

## Context

Non-relational engines (phase 3: MongoDB, decision D-13) cannot use `VelQuery.Core`: it is built on `DbConnection`,
`DbCommand`, `DbDataReader` and SQL text. But most of what makes VelQuery trustworthy is not ADO.NET-specific: the error
contract, release-exactly-once cleanup that never hides the original failure, the single-operation session guard,
transaction-scope semantics and resource-owning streams. Re-implementing those per engine would let guarantees drift.

## Decision

1. **A `VelQuery.Engine` namespace in `VelQuery.Abstractions`** holds the engine-neutral kernel. It references no
   driver, so engine packages such as `VelQuery.MongoDB` depend on Abstractions plus their driver, never on Core.
   (A separate package was not created: PRD §19.1 rule 6 asks to keep the package graph minimal.)

   | Type | Role |
   |---|---|
   | `IVelQueryErrorClassifier` | Engine knowledge: which exceptions come from the driver, and which mean timeout/cancellation |
   | `VelQueryErrors.Translate` | The one implementation of the error contract (VQ-FR-026) |
   | `AsyncDisposal.TryDisposeAsync` | Release a resource and report, not throw, its failure |
   | `SessionGuard` | Reject overlapping operations on a session-owned resource |
   | `IVelQueryTransactionScope` | Commit explicit; dispose without commit rolls back |
   | `ResourceOwningAsyncEnumerator<T>` | Stream base: open once, release exactly once (completed vs. early exit), translate failures, never hide them with release failures |

2. **The ADO.NET engine uses the kernel.** `IVelQueryProvider` extends `IVelQueryErrorClassifier` (with a default
   `IsProviderException => exception is DbException`, so existing providers are unchanged); `VelQuerySession` translates
   through `VelQueryErrors`, guards with `SessionGuard`, disposes through `AsyncDisposal`; `VelQueryTransaction`
   implements `IVelQueryTransactionScope`.
3. **Hot paths stay specialized.** The ADO.NET stream keeps its synchronous fast path and command-cancelling cleanup
   instead of deriving from `ResourceOwningAsyncEnumerator<T>`; it follows the same contract and is covered by the same
   lifecycle tests.
4. **What stays ADO.NET-only:** `QueryDescriptor` SQL text and `ParameterBinder` (`DbCommand`), `IRowMapper<T>`
   (`DbDataReader`), `IVelQueryProvider` dialect members, `ConnectionContext`. Engines define their own contract,
   binder and mapper shapes (for MongoDB: generated BSON serializers and typed filter/pipeline contracts).

## Consequences

- No public API break for SQL users; all suites pass unchanged (533 unit, 125 SQL Server, 31 PostgreSQL, generator).
- CPU per operation is unchanged within noise (`--cpu-profile`, 2026-10-20: VelQuery 0.98–1.04× raw ADO.NET; Dapper
  1.01–1.12×; Dapper.AOT 0.95–1.06×).
- The kernel is contract-tested with a fake non-ADO.NET engine (`EngineKernelTests`), the shape phase-3 adapters use.
- The refactor exposed a real hazard: `failure ??= await TryDisposeAsync(x)` skips the dispose once an earlier one
  failed. Disposals now always run before coalescing; existing cleanup-failure tests caught it.

## Alternatives considered

- **Separate `VelQuery.Engine` package.** Cleaner layering, but another package to version; revisit if Abstractions
  grows engine-specific baggage.
- **Generic `VelQuerySession<TEngine>`.** One session type for all engines; rejected because SQL, document and
  key-value APIs differ too much for one public surface, and it would force allocations/indirection on the SQL hot path.
- **Copy the rules into each engine.** Fastest to start; rejected because guarantees would drift.

## Open questions

- Shared conformance test kit (lifecycle, cancellation, error contract) that every engine adapter must pass.
- Neutral diagnostics hooks (query identity, timings) once VQ-FR-043 is implemented.
