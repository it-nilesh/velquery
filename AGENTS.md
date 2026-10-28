# AGENTS.md

Guidance for AI coding agents and contributors working in this repository.

## Project context

VelQuery is a query-first .NET 10 SQL execution engine: typed execution, source-generated binders/mappers,
compile-time SQL diagnostics, and (later, out-of-band) AI query intelligence. It is **not** an ORM.
The requirements baseline is [docs/prd/VelQuery_PRD_v1.0.md](docs/prd/VelQuery_PRD_v1.0.md), amended by
[v1.1 (multi-database support)](docs/prd/VelQuery_PRD_v1.1_Amendment.md); requirement IDs such as `VQ-FR-013` and
`VQ-DB-002` refer to them. The database roadmap and support tiers are in [docs/database-support.md](docs/database-support.md). Decisions are recorded in [docs/architecture/adr/](docs/architecture/adr/).

## Layout

| Path | Purpose |
|---|---|
| `src/VelQuery.Abstractions` | Contracts shared by runtime, providers and generated code: `QueryDescriptor<TArgs,TRow>`, `IRowMapperFactory<T>`, `RowMappers<T>` registry, `ParameterSpec`/`ParameterWriter`, `IVelQueryProvider`, error types, `ValueConverter`; `Engine/` holds the engine-neutral kernel (error contract, cleanup, session guard, transaction scope, stream base) that every engine — ADO.NET or not — must use (ADR-0016) |
| `src/VelQuery.Core` | `VelQueryDataSource`, `VelQuerySession` (execution, ownership, transactions, streaming, multi-result), `QueryParameters`, opt-in reflection fallback mapper |
| `src/VelQuery.SqlServer` | `SqlServerProvider` (Microsoft.Data.SqlClient), `VelQuerySqlServer.CreateDataSource`, DI registration, `Bulk/` (SqlBulkCopy) |
| `src/VelQuery.PostgreSQL` | `PostgreSqlProvider` (Npgsql), `VelQueryPostgreSql.CreateDataSource`, DI registration, `Bulk/` (binary COPY) |
| `src/VelQuery.MySql` | `MySqlProvider` (MySqlConnector), `VelQueryMySql.CreateDataSource`, DI registration, `Bulk/` (MySqlBulkCopy, LOAD DATA LOCAL) |
| `src/VelQuery.Sqlite` | `SqliteProvider` (Microsoft.Data.Sqlite, `NativeConversions`), `VelQuerySqlite.CreateDataSource`, DI registration, `Bulk/` (transaction + prepared INSERT) |
| `src/VelQuery.MongoDB` | MongoDB adapter on the engine kernel (no Core dependency): data source, session, transactions, descriptors, generated BSON serializers, bulk insert (ADR-0019) |
| `src/VelQuery.Generators` | Roslyn incremental generator for `[VelQuerySql]` contracts (netstandard2.0) |
| `src/VelQuery.MongoDB.Generators` | Incremental generator for `[VelQueryMongo*]` contracts and VQ2xxx diagnostics (netstandard2.0) |
| `src/VelQuery.Analyzers` | VQ1xxx diagnostics (netstandard2.0) |
| `src/VelQuery.Generators/Schema` | Schema snapshot checks: query shape recognizer, snapshot reader, matcher (ADR-0006) |
| `src/VelQuery.Cli` | `velquery` dotnet tool: `schema snapshot` / `schema diff` (catalog reads only) |
| `tests/` | Unit, golden, analyzer, CLI (schema end-to-end on all four SQL engines), and SQL Server / PostgreSQL / MySQL / SQLite / MongoDB integration tests (xUnit v3) |
| `benchmarks/` | BenchmarkDotNet suites comparing raw ADO.NET, Dapper, Dapper.AOT and VelQuery |

## Build and test

```bash
dotnet build VelQuery.slnx            # warnings are errors
dotnet test --solution VelQuery.slnx  # integration tests need Docker (Testcontainers SQL Server, PostgreSQL, MySQL, MongoDB); SQLite needs none
dotnet test --solution VelQuery.slnx --report-xunit-trx --results-directory artifacts/test-results  # keep per-test results
```

- SDK is pinned exactly in `global.json` (.NET 10, `rollForward: disable`): the trimming/AOT packs in the lock files
  follow the SDK version, so a different SDK breaks `--locked-mode` restore. To move to a new SDK, change `global.json`
  and run `dotnet restore VelQuery.slnx --force-evaluate` in the same commit. Package versions live only in `Directory.Packages.props`; never put
  `Version=` on a `PackageReference`. Lock files are enabled: run `dotnet restore --force-evaluate` after changing packages.
- Every new project must be added to `VelQuery.slnx`.

## Code conventions

- File-scoped namespaces, `ImplicitUsings` disabled (explicit `using`s), nullable enabled, XML docs on public APIs in `src/`.
- Library code uses `ConfigureAwait(false)` on every await (CA2007 is an error in `src/`).
- Runtime libraries are `IsAotCompatible`; trimming/AOT warnings are errors. No `Reflection.Emit`, no
  `MakeGenericType`/`MakeGenericMethod`, no `Convert.ChangeType` on execution paths.
- Match the existing comment density: explain *why* and reference PRD requirement IDs; don't narrate code.

## Non-negotiable runtime rules (PRD §6, §9, §12, §13)

1. Every acquired connection/command/reader is disposed exactly once on success, exception, cancellation and early
   enumeration exit. Cleanup failures must not hide the original failure.
2. Never dispose a caller-owned connection; never commit or roll back a caller-owned transaction.
3. Never retry anything implicitly. Never invent affected-row counts.
4. Parameter values are always bound, never concatenated into SQL.
5. Exceptions and diagnostics carry query ID, column and types — never parameter values, row values or connection strings.
6. NULL into a non-nullable target fails in strict mode (default). Conversions are lossless only (`ValueConverter`).
7. Cache only immutable data (descriptors, plans). Never cache `DbCommand`/`DbParameter` across calls.
8. No AI, network or telemetry work on the query hot path. Generators/analyzers never touch a database or network.

## Definition of done

Acceptance tests automated and passing; lifecycle/cancellation scenarios covered; public API changes have XML docs;
no new hot-path allocations without benchmark evidence; `dotnet build` clean with warnings as errors.
