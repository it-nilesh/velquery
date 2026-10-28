# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/) (pre-1.0: minor versions may break).

## [Unreleased] — 0.1.0-alpha

### Added
- Apache License 2.0 (`LICENSE`) and `NOTICE`; packages ship the NOTICE file.
- **Schema-checked SQL** (ADR-0006): the build checks `[VelQuerySql]` contracts against an offline schema snapshot
  (`*.vqschema.json`, format `velquery.schema.v1`) for unknown tables and columns (VQ1005), missing or duplicate result
  columns (VQ1025, VQ1028), nullable and type mismatches (VQ1026, VQ1027) and INSERT/UPDATE write problems (VQ1029,
  VQ1030); VQ1011 marks SQL outside the checked subset and VQ1031 an unusable snapshot. Diagnostics point at the name
  inside the SQL string, also in multi-line raw strings. The build never contacts a database.
- Schema checks: several snapshots per project (one per database), chosen by dialect or
  `[VelQuerySql(Schema = "...")]` (VQ1033 for an unknown name); stored procedure contracts are checked against the
  snapshot's procedures and parameters; VQ1032 (info) shows unknown names that may be columns instead of hiding them.
  Snapshots now include stored procedures and functions (`routines`), and `schema diff` compares them.
- Command batches: `session.CreateBatch(id).Add(sql, parameters)...ExecuteAsync()` sends several commands in one round
  trip (`DbBatch`) on SQL Server and PostgreSQL and returns each command's affected rows as the driver reports them.
  MySQL (whose driver reports only a batch total) and SQLite run the commands one at a time on one connection. New
  `ProviderCapabilities.Batches`.
- SQL Server table-valued parameters: `SqlTableType<T>.Create("dbo.IdList").Column(...)` and `.Rows(items)` stream
  rows without a `DataTable` (AOT-safe), for runtime SQL and generated contracts (new `IProviderParameterValue`).
- Benchmarks: EF Core 10 (pooled context, no tracking, compiled queries). It allocates 1.3–2.6× VelQuery per query;
  time differences were within the error of the short run.
- `VelQuery.Cli` dotnet tool (`velquery`): `schema snapshot` exports a deterministic snapshot from SQL Server,
  PostgreSQL, MySQL/MariaDB or SQLite using catalog views only; `--check` and `schema diff` detect schema drift in CI
  and classify breaking changes.
- Core engine: `VelQueryDataSource`/`VelQuerySession` with explicit connection and transaction ownership, buffered,
  First/Single, scalar, execute, streaming and multi-result operations, per-call timeouts, strict lossless mapping and a
  value-free error contract.
- Engine-neutral kernel (`VelQuery.Engine`): error translation, cleanup, session guard, transaction scope and stream
  base shared by all engines (ADR-0016).
- `[VelQuerySql]` source generator and VQ1xxx analyzers (parameter checks, unsafe SQL construction, mapping shape).
- Providers: SQL Server, PostgreSQL, MySQL/MariaDB, SQLite, and a generic ADO.NET tier; native bulk insert for each
  (SqlBulkCopy, binary COPY, LOAD DATA LOCAL, prepared INSERT) with all-or-nothing defaults.
- MongoDB adapter (`VelQuery.MongoDB`) with `[VelQueryMongo*]` contracts, VQ2xxx build-time checks including NoSQL
  injection rules, and generated reflection-free BSON serializers (ADR-0019).
- Native AOT smoke test (SQLite, PostgreSQL, MySQL verified on osx-arm64); CI workflow and Dependabot configuration.
- Dialect-aware build-time SQL scanning (`VelQuerySqlDialect` MSBuild property, set by each provider package, or
  `[VelQuerySql(Dialect = ...)]`) for SQL Server, PostgreSQL, MySQL, SQLite and generic SQL; diagnostics VQ1022
  (unbound placeholder style), VQ1023 (inconsistent placeholder casing) and VQ1024 (unknown dialect).
- `IVelQueryProvider.RegisterCancellation`: SQLite cancellation now interrupts running statements (`sqlite3_interrupt`).
- MongoDB writes run as raw `insert`/`update`/`delete` commands: server-side `maxTimeMS` on every write, and bulk
  inserts built into pooled buffers (1M documents 34% faster than the driver's `InsertMany`, 73% less allocation).
  Write errors surface as `MongoWriteCommandException` (replaces the driver's `MongoBulkWriteException`/`MongoWriteException`
  as the inner exception).

### Fixed
- T-SQL batches that `DECLARE` local variables no longer report them as undeclared parameters (VQ1001).
- PostgreSQL `INSERT ... OVERRIDING SYSTEM VALUE` no longer reports writes to identity columns (VQ1030).
- Schema checks (code review): `SELECT *` over `JOIN ... USING`/`NATURAL JOIN` no longer reports the shared column
  twice (VQ1028); tables created earlier in the batch, unknown keywords/functions (`UTC_TIMESTAMP`, `GLOB`) and system
  columns (`rowid`, `ctid`) no longer report VQ1005; a schema file picked up twice counts once (VQ1031);
  `DECLARE @a int = CASE ... END, @b int` declares both variables. SQL analysis now runs only when a snapshot exists
  and can no longer stop code generation.
- `velquery schema snapshot`: SQL Server CLR types (`geography`, `geometry`, `hierarchyid`) no longer abort the export;
  PostgreSQL materialized views are exported. `schema diff` reports unbounded to bounded sizes as breaking.

### Security
- Pinned `System.Security.Cryptography.Pkcs` 9.0.21 (CVE-2026-84008) for the Microsoft.Data.SqlClient dependency.

### Known limitations
- SQL Server is not Native AOT compatible (Microsoft.Data.SqlClient 7.1.1); MongoDB.Driver is not AOT-safe.
- No provider is "verified" in CI yet; all suites pass locally.
