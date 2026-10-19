# Provider compatibility

Tracks what VelQuery supports per database provider, what has been verified, and on which exact versions (PRD §10,
§29 item 7). **A feature is "verified" only when an automated test on the listed driver/server/OS passed.** Code that
exists but has no passing test on a listed environment is "implemented, not yet verified". Nothing here is a support
claim for an untested combination (PRD §10.1 "important qualification").

Last updated: 2026-10-11 (VelQuery 0.1.0-alpha). For the full list of planned databases and support tiers see
[database-support.md](database-support.md).

## Legend

| Mark | Meaning |
|---|---|
| Verified | Automated test passed on the environment named in the row's evidence column |
| Smoke | Exercised end-to-end with result checks by the benchmark suite's global setup (not a semantics test suite) |
| Implemented | Code exists; not yet verified by a test on a listed environment |
| Not yet verified | Not tested at all; no claim |
| Not started | No implementation |
| Unsupported | Deliberately not supported (see ADR) |

## Environments seen so far

| ID | Server | Driver | Runtime / OS | Notes |
|---|---|---|---|---|
| E1 | Azure SQL Edge 1.0.7 (SQL Server engine 15.0.2000.1574, arm64) in Docker (Colima) | Microsoft.Data.SqlClient 7.1.1 | .NET 10.0.5, macOS 26.5.1 arm64 (JIT) | Benchmark run 2026-10-10. SQL Edge is *not* SQL Server 2022; the x64 SQL Server 2022 image does not start under QEMU on this host. |

SQL Server 2022 (x64, `mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04`), Azure SQL Database and Windows hosts:
**not yet verified**.

## SQL Server (wave 1) — `VelQuery.SqlServer`, Microsoft.Data.SqlClient 7.1.1

| Capability | PRD | Status | Evidence / notes |
|---|---|---|---|
| Buffered query (`QueryAsync`) | VQ-FR-002 | Smoke (E1) | 100 and 1,000 rows identical to raw ADO.NET, descriptor and runtime-SQL paths |
| First / Single variants | VQ-FR-003/004 | Smoke (E1) for `QuerySingleOrDefaultAsync`; others Implemented | 0/2+ row behaviour not yet verified |
| Scalar (`ExecuteScalarAsync`) | VQ-FR-005 | Smoke (E1) for `int` | NULL / no-row semantics not yet verified |
| Non-query (`ExecuteAsync`) | VQ-FR-006 | Implemented | |
| Typed parameters: int, nvarchar(n), decimal(p,s), datetime2, bit | VQ-FR-007/008 | Smoke (E1) | Explicit size/precision/scale via `ParameterSpec` and `QueryParameters` |
| String/binary size bucketing (4000/8000/MAX) | §9.4 | Implemented | Plan-cache behaviour not yet verified |
| Other types (Guid, DateTimeOffset, binary, enums, DateOnly/TimeOnly) | VQ-FR-014 | Implemented | `DateOnly`/`TimeOnly` conversion added; not yet integration-tested against `date`/`time` columns |
| Unsigned integer / `sbyte` parameters | — | Unsupported by SqlClient | Fail at execution time (ADR-0005) |
| Cancellation (connect / execute / read) | VQ-FR-009 | Implemented | Not yet verified |
| Command timeout precedence | VQ-FR-010 | Implemented | Provider timeout (`Number -2`) → `VelQueryTimeoutException`; not yet verified by a test. One unexplained provider timeout was observed during a streaming benchmark on E1 (see `benchmarks/results/2026-10-10-apple-m5pro-shortrun/environment.md`) |
| Caller-owned connection / transaction | VQ-FR-001/011 | Implemented | Not yet verified |
| Session transaction (`BeginTransactionAsync`) | VQ-FR-012 | Implemented | Not yet verified |
| Streaming (`StreamAsync`), full enumeration | VQ-FR-013 | Smoke (E1) | 100,000 rows, checksum identical to raw ADO.NET |
| Streaming early exit / consumer exception | VQ-FR-013/027 | Implemented | Cancel-before-dispose; not yet verified |
| Multiple result sets | VQ-FR-021 | Implemented | Not yet verified |
| Stored procedures, output / input-output parameters | VQ-FR-023 | Implemented (runtime SQL only) | Descriptors have no output-parameter hook yet; not yet verified |
| Identifier quoting (`QuoteIdentifier`) | VQ-FR-042 | Implemented | |
| Prepared commands | VQ-FR-040, D-11 | Not started | Capability flag advertised, not used |
| Table-valued parameters | §9.4 | Not started | No implicit `IN` list expansion (by design) |
| Generated binders/mappers (`[VelQuerySql]`) | VQ-FR-018/019 | In progress | Generator being built; hand-written descriptor used in benchmarks |

### Native AOT (SQL Server)

| RID | Auth mode | Status |
|---|---|---|
| linux-x64 | SQL authentication | Not yet verified |
| linux-x64 | Microsoft Entra ID (any flavour) | Not yet verified |
| win-x64 | SQL authentication | Not yet verified |
| win-x64 | Integrated (SSPI) | Not yet verified |
| win-x64 | Microsoft Entra ID | Not yet verified |
| osx-arm64 / linux-arm64 | any | Not yet verified |

The VelQuery runtime assemblies are built with `IsAotCompatible=true` and trimming/AOT warnings as errors; that says
nothing about SqlClient's own AOT behaviour per authentication mode (PRD §10.1, [R6]). No AOT publish smoke test exists
yet (`tests/VelQuery.NativeAot.Smoke` is planned).

### Authentication matrix (JIT)

| Mode | Status |
|---|---|
| SQL authentication (user/password) | Smoke (E1, benchmarks only) |
| Integrated / Kerberos | Not yet verified |
| Microsoft Entra ID (password, service principal, managed identity, default) | Not yet verified |
| Encrypt=Strict / certificate validation | Not yet verified (benchmarks use `Encrypt=Optional;TrustServerCertificate=true` on loopback) |

## PostgreSQL (wave 2) — `VelQuery.PostgreSQL`, Npgsql 10.0.3

Evidence: `tests/VelQuery.PostgreSQL.IntegrationTests` (31 tests) against the official `postgres:17-alpine` image,
which runs natively on ARM64 and x64 (the real engine, unlike the SQL Edge stand-in used for SQL Server on ARM64).
Status is **preview** until the full parity matrix passes on CI (PRD D-04).

| Capability | Status | Notes |
|---|---|---|
| Buffered query, First/Single/OrDefault, scalar, execute | Verified | `count(*)` is `bigint`; narrowing to `int` is range-checked |
| Generated `[VelQuerySql]` contracts (SequentialAccess, shuffled SELECT order, strict NULL) | Verified | Same contracts as SQL Server; lower-case column names match case-insensitively |
| Types: int2/4/8, numeric, bool, text (Unicode), uuid, timestamp, timestamptz, date, time, bytea | Verified | `timestamptz` → UTC `DateTime` → `DateTimeOffset` (lossless); `date` → `DateOnly`; `time` → `TimeOnly`. `numeric` beyond .NET `decimal` range is a driver error |
| Parameters: inference, NULL, `DateTimeOffset` | Verified | Non-zero offsets are sent as the same instant in UTC (timestamptz stores instants) |
| Injection resistance | Verified | Includes dollar-quote payloads |
| Multiple result sets; error in a later statement | Verified | |
| Procedures via `AsStoredProcedure()` (CALL), INOUT parameters | Verified | PostgreSQL functions are called with plain `SELECT` |
| Command timeout / caller cancellation | Verified | Timeout → `VelQueryTimeoutException` (NpgsqlException + TimeoutException, or SQLSTATE 57014) |
| Streaming 100k rows; early exit; pool hygiene | Verified | Pool of 1–2 connections after faults, cancellations and early exits |
| Session transactions; caller-owned connection/transaction | Verified | |
| Bulk insert (`BulkInsertAsync`, binary COPY) | Verified | Always all-or-nothing; constraints and triggers always apply. 1M rows ≈ 0.60 s vs 0.58 s raw Npgsql COPY on the dev VM |
| Generator SQL scanning of PostgreSQL syntax | Partial | `@name` placeholders and `::` casts work; the scanner is T-SQL-lexical, so placeholders inside `ARRAY[...]` or dollar-quoted bodies are not seen (VQ1002 false positive) |
| Arrays / `ANY(@ids)` parameters | Not yet verified | Npgsql supports them; no VelQuery-specific API or tests yet |
| Native AOT (any RID / auth) | Not yet verified | Npgsql supports NativeAOT via `NpgsqlSlimDataSourceBuilder`; `CreateDataSource(NpgsqlDataSource)` accepts one |

## SQLite (phase 1) — `VelQuery.Sqlite`, Microsoft.Data.Sqlite 10.0.13 (SQLite 3.53.3)

Evidence: `tests/VelQuery.Sqlite.IntegrationTests` (70 tests, temp-file / shared-cache databases, no Docker) on .NET 10,
macOS arm64. Status **preview** until CI.

| Capability | Status | Notes |
|---|---|---|
| Buffered query, First/Single/OrDefault, scalar, execute | Verified | Integers are Int64; narrowing is overflow-checked. DDL `ExecuteAsync` returns the driver's stale `sqlite3_changes()` |
| Generated `[VelQuerySql]` contracts (shuffled SELECT order, strict NULL, scalar, non-query) | Verified | Same contract shapes as SQL Server/PostgreSQL |
| Types via NativeConversions: integers, bool, decimal (TEXT), string, Guid (TEXT/BLOB), DateTime, DateTimeOffset, DateOnly, TimeOnly, TimeSpan, byte[], double/float, char, enums (incl. nullable scalars) | Verified | DateTime.Kind is not stored; driver coercions apply (REAL→int truncates, non-numeric TEXT→0/false); decimals in NUMERIC columns become REAL — use TEXT, and prefer STRICT tables. The opt-in reflection fallback does not use native conversions |
| Parameters, injection resistance | Verified | Parameter names are case-sensitive; an unbound placeholder throws the driver's `InvalidOperationException` |
| Multiple result sets; error in a later statement | Verified | |
| Stored procedures / output parameters | Unsupported | SQLite has none |
| Lock timeout (SQLITE_BUSY/LOCKED) → `VelQueryTimeoutException` | Verified | `CommandTimeout` bounds lock waits only, not running statements |
| Caller cancellation | Verified (with limits) | Observed before execution and between rows; a single long step or lock wait is not interrupted (needs a provider cancellation hook) |
| Streaming 100k rows; early exit; connection hygiene | Verified | Every connection disposed (counted); the pool is unbounded and keyed by connection string |
| Session transactions; caller-owned connection/transaction | Verified | Plain `:memory:` is per connection — use a named shared-cache database or a pinned session |
| Bulk insert (`BulkInsertAsync`, one transaction + prepared INSERT) | Verified | All-or-nothing (savepoint inside a session transaction). 1M rows ≈ 0.71 s, same as raw Microsoft.Data.Sqlite; multi-row VALUES measured slower |
| Generator SQL scanning of SQLite syntax | Partial | `@name`, `"…"`, `[…]`, comments work; `?`, `:name`, `$name` and backticks are not understood; placeholders are matched case-insensitively but SQLite binds case-sensitively |
| Generic ADO.NET tier over `SqliteFactory` | Verified | No conversions unless `NativeConversions` is declared; BUSY/interrupt are plain provider errors |
| Native AOT | Not yet verified | Library builds with `IsAotCompatible` and no warnings |

## MySQL / MariaDB (phase 1) — `VelQuery.MySql`, MySqlConnector 2.6.2

Evidence: `tests/VelQuery.MySql.IntegrationTests` (58 tests) on `mysql:8.4` (8.4.11) and `mariadb:11` (11.8.9) via
Testcontainers, macOS arm64. Status **preview** until CI.

| Capability | Status | Notes |
|---|---|---|
| Buffered query, First/Single/OrDefault, scalar, execute | Verified | `count(*)` is BIGINT; narrowing range-checked |
| Affected rows | Verified | Default = rows found (matched), like SQL Server/PostgreSQL; `UseAffectedRows=true` = rows changed |
| Generated `[VelQuerySql]` contracts (SequentialAccess, shuffled order, strict NULL, backtick identifiers) | Verified | |
| Types: tinyint…bigint unsigned, decimal, tinyint(1)→bool, utf8mb4 text, char(36)→Guid, binary, datetime(6), timestamp, date→DateOnly, time→TimeOnly/TimeSpan, json, longblob, float/double | Verified | `BIT(n)`→`ulong` (not bool); DATETIME→`DateTimeOffset` only with `DateTimeKind=Utc`; BINARY(16)→Guid only with an explicit `GuidFormat`; DECIMAL beyond .NET range → driver `FormatException` |
| Parameters, injection resistance | Verified | Includes backslash/backtick payloads; under `NO_BACKSLASH_ESCAPES` set `NoBackslashEscapes=true` (otherwise backslashes are doubled — data changes, no injection) |
| Multiple result sets; error in a later statement | Verified | |
| Stored procedures with IN / OUT / INOUT | Verified | |
| User variables | Verified | Require `AllowUserVariables=true` |
| Command timeout / server statement limit / caller cancellation | Verified | Timeout: `CommandTimeoutExpired`, 3024 (MySQL), 1969 (MariaDB). An interrupted `SLEEP()` returns 1 on MySQL |
| Lock wait timeout (1205), deadlock (1213), KILL QUERY (1317) | Verified | `VelQueryProviderException` with the error number preserved (concurrency conflicts, not timeouts) |
| Streaming 100k rows; early exit; pool hygiene (1–2 connections) | Verified | |
| Session transactions; caller-owned connection/transaction | Verified | |
| Bulk insert (`BulkInsertAsync`, LOAD DATA LOCAL) | Verified | All-or-nothing; skipped rows/warnings fail with `MySqlBulkInsertException` (counts and codes only) and roll back the load (savepoint inside a session transaction); local infile disabled → `VelQueryUnsupportedCapabilityException`. 1M rows ≈ 1.29 s vs 1.32 s raw `MySqlBulkCopy` |
| Generator SQL scanning of MySQL syntax | Partial | Backticks and `"…"` OK; backslash-escaped quotes, `#` comments and `@user_vars` cause VQ1019/VQ1001 false positives |
| Native AOT | Not yet verified | |

## How to update this file

1. Add or update an environment row with exact server build, driver version, runtime and OS/RID.
2. Change a capability to "Verified" only with a link to the test (or CI job) that passed on that environment.
3. Record failures and exclusions explicitly; never delete a failed combination to make the table look green.
