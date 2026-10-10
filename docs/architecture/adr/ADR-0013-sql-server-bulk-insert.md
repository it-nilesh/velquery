# ADR-0013: SQL Server bulk insert

- **Status:** Proposed
- **Date:** 2026-10-11

## Context

One-row-per-round-trip inserts are bounded by network latency and one log flush per commit: about 1,500 rows/s on the
reference dev VM, so 1M rows take about 11 minutes with any library. PRD §2.3 keeps bulk frameworks out of Core GA,
but write-heavy users need an I/O-efficient path that keeps VelQuery's ownership and error guarantees.

## Decision

1. **Core extension point.** `IVelQuerySession.ExecuteOnConnectionAsync(queryId, state, operation, ct)` lends a
   `ConnectionContext` (connection, active transaction, resolved timeout) to provider-specific code under the usual
   acquire/release, cancellation, cleanup and error-translation rules. Core stays provider-neutral.
2. **`VelQuery.SqlServer.Bulk`.** `SqlBulkInsertPlan<T>` is an immutable, reusable plan: a quoted destination table
   (identifier parts validated, never interpolated raw) and typed column getters (`Column("Id", r => r.Id)`; no
   reflection). `session.BulkInsertAsync(plan, IEnumerable<T> | IAsyncEnumerable<T>, ct)` returns rows written.
3. **INSERT-like defaults**, unlike raw `SqlBulkCopy`: `Atomic = true` (one transaction when the session has none),
   `CheckConstraints = true`, `KeepNulls = true`. Inside a session transaction the load always enlists. `TableLock`,
   `FireTriggers`, `BatchSize`, `Timeout` are opt-in.
4. **Chunked DataTable staging.** Rows are staged into one reusable typed `DataTable` of `BatchSize` rows and written
   with `WriteToServerAsync(DataTable)`. Measured on SqlClient 7.1.1: feeding a custom `DbDataReader` to
   `WriteToServerAsync` cost 12–14 s client CPU per 1M rows (per-row continuations) versus about 1.1 s for chunked
   tables or the synchronous API; the synchronous API would block a thread-pool thread for the whole load.
5. Enum columns are rejected at plan build (SqlClient cannot bulk-copy CLR enums); map the underlying value.

## Consequences

- 1M rows: VelQuery 2.25 s (444K rows/s, 1.09 s CPU, 683 MB allocated) vs raw `SqlBulkCopy` 2.38 s (420K rows/s,
  1.05 s CPU, 752 MB) on the reference VM (`benchmarks --bulk-profile`).
- Client memory is bounded by one chunk, including for `IAsyncEnumerable<T>` sources.
- Atomic loads hold one transaction for the whole load (log growth); `Atomic = false` commits per chunk.
- Each chunk is a separate bulk operation; `BatchSize` therefore also sets round-trip granularity.

## Alternatives considered

- **Streaming `DbDataReader` source.** Lowest allocation in theory; rejected for the async CPU cost above.
- **Multi-row `INSERT ... VALUES` batches / TVPs.** Useful for mid-sized writes and upserts; TVP support remains an open
  item (PRD §9.4 lists it as a provider-specific extension).
- **Generator-emitted column maps.** Would remove delegate boxing per cell; deferred until the plan API settles.

## Open questions

- TVP parameters and `MERGE`-based upsert helpers.
- PostgreSQL `COPY` equivalent when the Npgsql provider lands.
