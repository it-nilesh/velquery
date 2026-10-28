# VelQuery — Project status report

**Date:** 2026-10-21 · **Version:** 0.1.0-alpha (pre-release) · **Repository:** local only (not yet hosted)

## 1. Summary

VelQuery is a query-first .NET 10 data-access library: hand-written queries checked at build time, generated
reflection-free mapping, strict runtime semantics and native bulk loading. In 12 days it went from an empty folder to
five working database integrations, a source generator per query language, benchmarks, and 1,136 automated tests.

| Area | Status |
|---|---|
| Core engine (sessions, ownership, transactions, streaming, errors) | Done |
| SQL providers: SQL Server, PostgreSQL, MySQL/MariaDB, SQLite + generic ADO.NET tier | Done — **Preview** |
| MongoDB adapter (first non-relational database) | Done — **Preview** |
| Build-time checks: `[VelQuerySql]` (VQ1xxx, dialect-aware) and `[VelQueryMongo*]` (VQ2xxx) | Done |
| Schema-checked SQL: snapshot export (`velquery` tool), build-time table/column/type/nullability checks, drift check | Done — verified end to end on all four SQL engines |
| Native bulk insert for every database | Done |
| Native AOT | Verified for SQLite, PostgreSQL, MySQL; blocked for SQL Server and MongoDB by their drivers |
| CI pipeline | Written, **not run** (repository not hosted) |
| NuGet packages | Pack correctly, **not published** |

**Bottom line:** feature-complete for a first preview and measured as fast as or faster than the alternatives, but not
production-ready until it runs in CI against real servers and passes the PRD's release gates.

## 2. What was built

| Component | Purpose |
|---|---|
| `VelQuery.Abstractions` | Shared contracts and the engine-neutral kernel (error contract, cleanup, session guard, transaction scope, stream base) |
| `VelQuery.Core` | ADO.NET engine: sessions, buffered/single/scalar/stream/multi-result operations, transactions, per-call timeouts |
| `VelQuery.SqlServer` | Microsoft.Data.SqlClient provider, SqlBulkCopy bulk insert |
| `VelQuery.PostgreSQL` | Npgsql provider, binary COPY bulk insert |
| `VelQuery.MySql` | MySqlConnector provider (MySQL and MariaDB), LOAD DATA bulk insert with silent-row-drop protection |
| `VelQuery.Sqlite` | Microsoft.Data.Sqlite provider, native type conversions, statement interruption on cancel |
| `VelQuery.MongoDB` | MongoDB adapter: typed contracts, generated BSON serializers, transactions, command-based writes |
| `VelQuery.Generators` / `VelQuery.MongoDB.Generators` / `VelQuery.Analyzers` | Build-time code generation and diagnostics (44 rules) |
| `VelQuery.Cli` | `velquery` tool: schema snapshot export and drift diff (ADR-0006) |
| `samples/QuickStart` | Minimal API + data library with a repository layer and a committed schema snapshot |
| `benchmarks/` | BenchmarkDotNet suites plus CPU, allocation and bulk profiles |

Size: about 14,700 lines of library code and 17,800 lines of tests; 38 commits; 12 architecture decision records.

## 3. Roadmap progress (PRD amendment v1.1)

| Phase | Scope | Status |
|---|---|---|
| 1. SQL everywhere | Generic ADO.NET tier, SQLite, MySQL/MariaDB (plus SQL Server and PostgreSQL) | **Complete** |
| 2. Engine-neutral core | Shared rules every engine must follow (ADR-0016) | **Complete** — no performance regression |
| 3. Non-relational pilot | MongoDB (decision D-13) | **Complete** |
| 4. Expand by demand | Cosmos DB, Redis, graph, wide-column, search, vector | Not started |

## 4. Release gates (PRD §22)

| Gate | Status | What is missing |
|---|---|---|
| A — Architecture readiness | Met | — |
| B — v0.1 MVP | Met locally | Quick start not yet reproduced by an outside developer |
| C — v0.5 preview | Mostly met | AOT matrix in CI; generated-path evidence published from CI |
| D — v1.0 Core GA | Not met | CI on real servers, public API freeze, signed packages, 3 design-partner teams |

## 5. Blockers and decisions needed

| # | Item | Owner | Why it matters |
|---|---|---|---|
| 1 | Host the repository (GitHub) | Product owner | CI cannot run; nothing can move from "Preview" to "Verified"; no backup |
| 2 | Run the suite on real SQL Server 2022 (x64) | Engineering (needs #1) | All SQL Server tests ran on Azure SQL Edge on ARM64 |
| 3 | ~~License decision~~ | Done | Apache-2.0 adopted: `LICENSE` and `NOTICE` added (2026-10-29) |
| 4 | Trademark and NuGet name clearance for "VelQuery" | Legal | PRD risk item |
| 5 | Design partners (3–5 teams) and API freeze | Product owner | Required for Gate D |

## 6. Recommended next steps

1. Host the repository and let CI run (build, all suites on x64 Linux with SQL Server 2022, AOT smoke on three OSes).
2. Publish a signed 0.1.0-alpha to a private feed and onboard the first design partner with the QuickStart sample.
3. A full benchmark run (default job) on an uncontended x64 SQL Server 2022 host, to resolve time against EF Core and
   the open stream-100k gap. EF Core is now in the suite: it allocates 1.3–2.6× VelQuery per query.
4. Done since the last report: EF Core benchmarks, command batches (`DbBatch`), SQL Server table-valued parameters,
   schema checks for stored procedures and several databases per project.
