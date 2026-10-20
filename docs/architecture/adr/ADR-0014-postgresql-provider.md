# ADR-0014: PostgreSQL provider

- **Status:** Proposed
- **Date:** 2026-10-11

## Context

PRD EP-06 / D-04: PostgreSQL follows SQL Server behind parity gates. The runtime, descriptors and generated code are
provider-neutral (`IVelQueryProvider`), so the provider must add dialect behavior without changing contracts.

## Decision

- `VelQuery.PostgreSQL` on Npgsql 10. `VelQueryPostgreSql.CreateDataSource(connectionString)` builds an
  `NpgsqlDataSource` (Npgsql pooling, no VelQuery pool); an overload accepts an application-built data source (type
  mappings, `NpgsqlSlimDataSourceBuilder` for Native AOT). `AddVelQueryPostgreSql` mirrors the SQL Server DI helper.
- Parameter rules are recorded in ADR-0005. Errors: `NpgsqlException` wrapping `TimeoutException`, or SQLSTATE 57014
  without caller cancellation, is a timeout; everything else is a `VelQueryProviderException` with the original
  `PostgresException` (SQLSTATE preserved) as the inner exception.
- Bulk insert uses `COPY ... FROM STDIN (FORMAT BINARY)` with per-column typed writers (`importer.WriteAsync<TValue>`, no
  per-cell boxing). A COPY is one statement, so loads are always atomic and constraints/triggers always apply; it runs
  inside the session transaction when one is active.
- Generated mappers' strict fast path cannot rely on `IsDBNull` after a failed typed read: Npgsql rejects re-reading a
  column under SequentialAccess. The filter falls back to the failure type (a type-matched getter that throws
  `InvalidCastException`/`SqlNullValueException` can only have met NULL). Found by this suite.
- `ValueConverter` gains one lossless rule needed for Npgsql: UTC `DateTime` → `DateTimeOffset` (zero offset).

## Consequences

- The same `[VelQuerySql]` contracts run unchanged on both providers (verified).
- Status stays **preview** until CI runs the parity matrix (D-04); arrays, NativeAOT and a PostgreSQL-aware SQL scanner
  are open.
- Table and column names in bulk plans are quoted as given, so they are case-sensitive (PostgreSQL folds unquoted
  identifiers to lower case).

## Alternatives considered

- Positional `$1` placeholders: native to PostgreSQL but would make descriptors dialect-specific; Npgsql's `@name`
  rewriting keeps one SQL style across providers for the common subset.
- Text-format COPY or multi-row `INSERT`: slower and needs escaping; binary COPY is Npgsql's fastest path.

## Open questions

- Array parameters (`= ANY(@ids)`) API and generator support.
- *Resolved 2026-10-21:* dialect-aware SQL scanning (`postgresql`: dollar quotes, `E''` strings, `ARRAY[...]`, `$1` → VQ1022).
- PostgreSQL rows in the benchmark suite (PRD §14.1 requires separate SQL Server and PostgreSQL results).
