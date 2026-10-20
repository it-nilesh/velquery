# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/) (pre-1.0: minor versions may break).

## [Unreleased] — 0.1.0-alpha

### Added
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

### Security
- Pinned `System.Security.Cryptography.Pkcs` 9.0.21 (CVE-2026-84008) for the Microsoft.Data.SqlClient dependency.

### Known limitations
- SQL Server is not Native AOT compatible (Microsoft.Data.SqlClient 7.1.1); MongoDB.Driver is not AOT-safe.
- No provider is "verified" in CI yet; all suites pass locally.
