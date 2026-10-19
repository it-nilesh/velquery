# ADR-0017: SQLite provider

- **Status:** Proposed
- **Date:** 2026-10-20
- **Implements:** PRD amendment v1.1 EP-17; ADR-0015 phase 1

## Context

SQLite is embedded, dynamically typed and has no bulk API. It stores `DateTime`, `decimal`, `Guid` and `bool` as TEXT,
REAL or INTEGER, so `GetValue()` returns `string`/`double`/`long`; VelQuery's strict `ValueConverter` (lossless only,
never parses strings) would reject them. Microsoft.Data.Sqlite's typed getters perform documented conversions instead.

## Decision

- `VelQuery.Sqlite` on Microsoft.Data.Sqlite. Dialect `sqlite`, `@name` parameters, `"` quoting; `ProviderDbType` is
  applied as `SqliteType` (for example a Guid stored as BLOB).
- The provider declares **`ProviderCapabilities.NativeConversions`** (added in phase 1 groundwork): generated and scalar
  mappers, and `ExecuteScalar`, read mismatched storage types through `GetFieldValue<T>`. Strict NULL handling is
  unchanged. `Nullable<enum>` scalars use `ValueConverter`'s integral-to-enum rule because drivers cannot produce
  `Nullable<enum>` themselves.
- Errors: SQLITE_BUSY/LOCKED (incl. extended codes) → timeout (the driver retries until `CommandTimeout`);
  SQLITE_INTERRUPT not caused by the caller → cancelled; everything else → provider error.
- Bulk insert: one `BEGIN IMMEDIATE` transaction (or a SAVEPOINT inside a session transaction) plus one prepared INSERT
  re-bound per row; the command lives for one call only (AGENTS rule 7). Multi-row VALUES chunks were measured slower
  (linear parameter-name lookup) and are not offered.

## Consequences

- 70 integration tests pass locally with no Docker; bulk 1M rows ≈ 0.71 s, equal to a hand-written raw loop.
- Driver coercions are inherited where SQLite's storage model makes them unavoidable (REAL→int truncates, non-numeric
  TEXT→0/false, `DateTime.Kind` not stored). Recommendation: STRICT tables and TEXT columns for decimals.
- Cancellation is only observed before execution and between rows: `SqliteCommand.Cancel()` is a no-op and a single
  long step is not interrupted.
- The opt-in reflection fallback mapper does not use native conversions (it only has `GetValue()`); use generated or
  registered mappers on SQLite.

## Open questions

- Provider cancellation hook (for example `IVelQueryProvider` registering `sqlite3_interrupt` on the token) so running
  statements can be interrupted; touches Core execution paths.
- Dialect-aware scanning: `?`, `?NNN`, `:name`, `$name` placeholders and case-sensitive binding (VQ-DB-008).
