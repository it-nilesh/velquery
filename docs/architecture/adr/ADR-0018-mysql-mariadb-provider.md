# ADR-0018: MySQL / MariaDB provider

- **Status:** Proposed
- **Date:** 2026-10-20
- **Implements:** PRD amendment v1.1 EP-16; ADR-0015 phase 1

## Context

MySQL and MariaDB share a wire protocol and MySqlConnector, but differ in error codes and some type behaviors. Their
native bulk path (`LOAD DATA LOCAL INFILE` via `MySqlBulkCopy`) behaves like `INSERT IGNORE` even in strict mode:
rows violating keys or constraints are skipped and NULL into NOT NULL becomes `''` with only a warning.

## Decision

- `VelQuery.MySql` on MySqlConnector (`MySqlDataSource`). Dialect `mysql`, `@name` parameters, backtick quoting
  (64 chars). No string size bucketing: MySQL has no shared plan cache keyed on parameter metadata. No
  `NativeConversions`: every supported type is an exact match or a lossless `ValueConverter` rule.
- Errors: `CommandTimeoutExpired`, 3024 (MySQL `max_execution_time`) and 1969 (MariaDB `max_statement_time`) →
  timeout. Lock wait timeout (1205), deadlock (1213) and `KILL QUERY` (1317) stay provider errors with the error number
  preserved — they are concurrency conflicts, consistent with SQL Server and PostgreSQL.
- Affected rows default to rows **found** (MySqlConnector default), matching the other providers.
- Bulk insert never drops rows silently: the load runs in its own transaction (or a SAVEPOINT inside a session or
  caller transaction); after `BulkCopyFailed`, or if `RowsInserted` differs from rows sent, or any non-Note warning
  appears in `SHOW WARNINGS`, it throws `MySqlBulkInsertException` (counts and codes only, wrapped in
  `VelQueryProviderException`) and rolls the load back. Plans with `Guid`/`byte[]` columns check destination column types
  first (MariaDB stores a `Char36` Guid into `BINARY(16)` as NULL without a warning). Local infile disabled on either
  side → `VelQueryUnsupportedCapabilityException`.
- Rows stream through a `DbDataReader` adapter: unlike SqlBulkCopy (ADR-0013), MySqlBulkCopy showed no async CPU penalty.

## Consequences

- 58 integration tests pass on `mysql:8.4` and `mariadb:11`; bulk 1M rows ≈ 1.29 s vs 1.32 s raw `MySqlBulkCopy`, 0.26 s
  client CPU (vs 15 min one INSERT per row).
- Bulk requires `AllowLoadLocalInfile=true` and server `local_infile=ON` — documented, with a clear error.
- `BIT(n)` maps to `ulong` (not bool); DATETIME→`DateTimeOffset` requires `DateTimeKind=Utc`; BINARY(16) GUIDs require
  an explicit `GuidFormat`.

## Open questions

- Dialect-aware scanning: backslash-escaped quotes, `#` comments, `@user_vars`, executable comments (VQ-DB-008).
- TiDB and other MySQL-compatible engines need their own test runs before being listed as supported.
