# VelQuery benchmarks

BenchmarkDotNet suite comparing **raw ADO.NET**, **Dapper**, **Dapper.AOT**, **EF Core** and **VelQuery** on SQL Server
(PRD §14.1–14.2, §29 item 3). The suite contract is [`suites/core.yaml`](suites/core.yaml); the executable form is
[`VelQuery.Benchmarks`](VelQuery.Benchmarks).

> Results are specific to the machine, OS, container runtime, database build, driver and load at the time of the run.
> They are evidence for engineering decisions, not product claims. Do not quote a number without its
> `environment.md`, and do not compare runs from different environments (PRD §14.2 "No marketing claim until verified").

## What is compared

| ID | Implementation |
|---|---|
| `raw-adonet` (baseline) | Hand-optimized SqlClient: fixed ordinals, typed getters, explicit `SqlDbType`/size/precision/scale, `SequentialAccess` |
| `dapper` | Dapper 2.x, call sites in a `[DapperAot(false)]` class: reflection/IL-emit path |
| `dapper-aot` | Identical call sites in a `[DapperAot(true)]` class: replaced at build time by Dapper.AOT interceptors |
| `velquery-descriptor` | Generated `[VelQuerySql]` contracts: VelQuery.Generators emits the binders and the `BenchRow` mapper (ordinals resolved once per reader, typed getters, columns read in order so `SequentialAccess` is used) |
| `velquery-runtime-sql` | `session.QueryAsync<T>(sql, QueryParameters)` etc.; the same mapper registered through `RowMappers<T>.Register` (no reflection fallback) |
| `efcore-compiled` | EF Core 10 in its fastest common setup: pooled `DbContext` factory, `NoTracking`, compiled queries, explicit column types. EF writes its own SQL, so the "query-text-identical" control does not apply to it |

Cases: `one-row-projection`, `scalar-count`, `buffered-100`, `buffered-1000`, `stream-100k` (warmup 2, 5 iterations),
`typed-params`. The SQL text for each case is in [`Model.cs`](VelQuery.Benchmarks/Model.cs) and the manifest.

## Running

Prerequisites: .NET SDK from `global.json`, Docker (or an existing SQL Server).

```bash
cd benchmarks/VelQuery.Benchmarks

# Quick sanity run (one invocation per benchmark, numbers meaningless):
dotnet run -c Release -- --filter '*OneRowProjection*' --job dry

# Short run of everything (low statistical confidence, a few minutes):
dotnet run -c Release -- --filter '*' --job short --artifacts ../results/$(date +%F)-<machine>-shortrun

# Full default job (much longer; use for anything you intend to publish):
dotnet run -c Release -- --filter '*' --artifacts ../results/$(date +%F)-<machine>-full

# Single case:
dotnet run -c Release -- --filter '*Stream100k*' --job short
dotnet run -c Release -- --list flat

# Not a benchmark: allocation-by-type view (GC AllocationTick samples) for stream-100k
dotnet run -c Release -- --alloc-profile
```

The host process starts **one** SQL Server container with Testcontainers.MsSql, creates `VelQueryBench`, seeds
100,000 rows from a fixed seed, writes `environment.json` into the artifacts folder, and then lets BenchmarkDotNet
launch one child process per benchmark. Children receive the connection string through `VELQUERY_BENCH_SQLSERVER`.

| Variable | Effect |
|---|---|
| `VELQUERY_BENCH_SQLSERVER` | Use this SQL Server instead of starting a container (the database is created/seeded if needed). |
| `VELQUERY_BENCH_SQLSERVER_IMAGE` | Override the container image. |
| `DOCKER_HOST`, `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE` | Needed for non-default Docker setups, e.g. Colima: `DOCKER_HOST=unix://$HOME/.colima/default/docker.sock`, `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock`. |

**Images.** x64 hosts use `mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04`. On arm64 hosts the SQL Server 2022
image does not start under QEMU (it fails with "Invalid mapping of address ... in reserved address space"), so the
suite uses `mcr.microsoft.com/azure-sql-edge:1.0.7` (SQL Server engine 15.x, arm64-native). Testcontainers.MsSql's
readiness check runs `sqlcmd` inside the container, which SQL Edge does not ship, so the suite waits for the
"ready for client connections" log line instead. SQL Edge results are **not comparable** with SQL Server 2022 results.

Output per run: `results/*-report-full.json` (all measurements, percentiles), `*-report-github.md`, `*.csv`, `*.html`,
the BenchmarkDotNet log, `environment.json` (server/driver/runtime versions) and, for committed runs, a hand-written
`environment.md` with caveats.

## Fairness controls

- **Same SQL text** for every library in a case (constants in `BenchSql`), same database, same data, same indexes.
- **Same connection handling:** one connection string (`Pooling=true; Min Pool Size=0; Max Pool Size=100; MARS=false`),
  every library opens a pooled connection per operation and disposes it. VelQuery gets it through
  `IVelQueryConnectionFactory` over SqlClient's `DbDataSource`; a new `VelQuerySession` is created per operation
  (as a DI-scoped session would be).
- **Same semantics:** single-row cases use single-or-default everywhere (read a second row to prove uniqueness);
  streaming uses each library's unbuffered API (`QueryUnbufferedAsync`, `StreamAsync`, a reader loop) and folds rows
  into a checksum.
- **Same parameter metadata in `typed-params`:** `int`, `nvarchar(100)` (explicit size), `decimal(18,4)` (explicit
  precision/scale), `datetime2`, `bit`. Plain Dapper uses `DynamicParameters` (its only way to declare these facets);
  Dapper.AOT uses a typed args class with `[DbValue]`; VelQuery uses `ParameterSpec`/`QueryParameters`.
- **No instrumentation:** no logging, no diagnostic listeners; VelQuery has no diagnostics on its path.
- **Result verification before measuring:** each benchmark process's global setup runs every library once and throws
  if any result differs from raw ADO.NET (rows, values, NULLs, checksum).
- **Known optimizations, not handicapped defaults:** the raw baseline uses `SequentialAccess` because Dapper and
  Dapper.AOT do; VelQuery's executor currently uses `CommandBehavior.Default` and is measured as is.
- **Isolation:** BenchmarkDotNet's default out-of-process toolchain, workstation non-concurrent GC, `MemoryDiagnoser`
  on. The database runs in a container on the same host (loopback), so client and server compete for CPU.

### Verifying the Dapper / Dapper.AOT split

Dapper.AOT intercepts every Dapper call in an enabled scope, which would silently turn the `dapper` baseline into a
second `dapper-aot`. The project sets `EmitCompilerGeneratedFiles=true`; after a build, check:

```bash
grep -n InterceptsLocation \
  VelQuery.Benchmarks/obj/Release/net10.0/generated/Dapper.AOT.Analyzers/Dapper.CodeAnalysis.DapperInterceptorGenerator/*.cs
```

Every `InterceptsLocation` line must point into `DapperLibraries.cs` lines inside `DapperAotQueries`, and none inside
`DapperQueries`. On 2026-10-10 there were exactly five, one per `DapperAotQueries` call site.

## Known limitations

- Single client only; no throughput-vs-concurrency curve (VQ-NFR-002) and no CPU-per-op metric yet.
- p95 is reported; p99 from a short run is not meaningful (too few samples). Use the full job for percentiles.
- Loopback container DB: network latency is not represented, and container/VM scheduling adds noise.
- No EF Core, RepoDB or LINQ to DB baselines yet (PRD §14.1 lists them).

## Recorded runs

| Run | What it shows |
|---|---|
| `results/2026-10-10-apple-m5pro-shortrun/` | **Historical baseline before the performance work**: hand-written VelQuery mapper, `CommandBehavior.Default`, `List<T>` buffering. Superseded — VelQuery's allocations dropped 29–44% afterwards (see the root README). Kept to show the before/after. |

Current numbers come from `--cpu-profile`, `--bulk-profile` and short BenchmarkDotNet runs and are summarised in the root
[README](../README.md); re-run them on your hardware rather than comparing across machines.
