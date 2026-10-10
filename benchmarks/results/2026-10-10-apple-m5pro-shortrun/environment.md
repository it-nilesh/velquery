# 2026-10-10 — Apple M5 Pro — ShortRun

**Low statistical confidence.** This is a BenchmarkDotNet `ShortRun` (1 launch, 3 warmup, 3 measured iterations;
stream-100k: 2 warmup, 5 measured), on a laptop, against a loopback Docker database that shared a 2-CPU VM with
other containers. It proves the suite runs end to end and gives a first baseline. It is **not** evidence for
any performance claim. Many differences below are smaller than the reported error (half of the 99.9% CI).

## Environment

| Item | Value |
|---|---|
| Host | Apple M5 Pro, 15 cores, 24 GB RAM, macOS 26.5.1 (25F80), arm64 |
| Runtime | .NET 10.0.5 (Arm64 RyuJIT), SDK 10.0.201, workstation non-concurrent GC, Release |
| BenchmarkDotNet | 0.15.8, default out-of-process toolchain, `MemoryDiagnoser`; process priority could not be raised (permission denied) |
| Database | Azure SQL Edge 1.0.7 Developer (SQL Server engine 15.0.2000.1574, ARM64), `mcr.microsoft.com/azure-sql-edge:1.0.7`, via Testcontainers.MsSql 4.16.0 |
| Why not SQL Server 2022 | The x64 `mssql/server:2022` image does not start under QEMU on this host ("Invalid mapping of address ... in reserved address space"). SQL Edge results must not be compared with SQL Server 2022 results. |
| Container runtime | Colima (Docker 29.2.1), Linux VM aarch64, **2 vCPU, 1.91 GB RAM**, loopback TCP (`127.0.0.1`) |
| Driver | Microsoft.Data.SqlClient 7.1.1 |
| Libraries | Dapper 2.1.89, Dapper.AOT 1.1.0, VelQuery 0.1.0-alpha (commit 161e55f) |
| Connection | `Pooling=true; Min Pool Size=0; Max Pool Size=100; MARS=false; Encrypt=Optional; TrustServerCertificate=true`, SQL authentication; one pooled open/close per operation for every library |
| Data | `dbo.BenchRows`, 100,000 rows, seed 20261010, every 10th `Email` NULL, statistics updated with FULLSCAN |
| Dapper.AOT split | Verified: 5 interceptors, all inside `DapperAotQueries`; none in `DapperQueries` |

Raw machine record: [`environment.json`](environment.json).

### Contention during the run

`docker-activity.log` samples running containers every 5 s. Another session's SQL Edge test container
(`azure-sql-edge:latest`) was running in **34 of 45 samples** (about 76% of the run) on the same 2-vCPU VM, alongside
several idle application containers. Expect inflated variance, especially for the larger cases.

## Results (mean, allocated per operation)

Ratios are against `raw-adonet` in the same case. Full data: `results/*-report-full.json`; tables:
`results/*-report-github.md`.

| Case | raw-adonet | dapper | dapper-aot | velquery-descriptor | velquery-runtime-sql |
|---|---|---|---|---|---|
| one-row-projection | 349.0 μs / 6.10 KB | 369.7 μs / 7.86 KB | 365.1 μs / 6.74 KB | 347.9 μs / 7.29 KB | 355.5 μs / 7.62 KB |
| scalar-count | 410.1 μs / 4.12 KB | 433.5 μs / 4.27 KB | 405.1 μs / 4.38 KB | 422.7 μs / 4.41 KB | 440.0 μs / 4.45 KB |
| buffered-100 | 480.1 μs / 27.79 KB | 467.3 μs / 37.16 KB | 477.1 μs / 28.58 KB | 452.1 μs / 38.37 KB | 498.0 μs / 38.35 KB |
| buffered-1000 | 1,061.2 μs / 233.08 KB | 1,235.3 μs / 312.81 KB | 1,188.7 μs / 233.85 KB | 1,333.8 μs / 381.64 KB | 1,214.4 μs / 384.68 KB |
| stream-100k | 41.26 ms / 21.70 MB | 41.80 ms / 29.31 MB | 57.67 ms / 21.69 MB | 53.14 ms / 36.64 MB | 49.59 ms / 37.27 MB |
| typed-params | 361.1 μs / 7.64 KB | 342.1 μs / 10.44 KB | 436.9 μs / 8.29 KB | 438.8 μs / 8.98 KB | 396.4 μs / 9.77 KB |

Error (99.9% CI half-width) is often larger than the differences: for example typed-params dapper-aot ±1,857 μs,
velquery-descriptor ±1,279 μs; stream-100k dapper-aot ±40.96 ms. Read the latency columns as "no resolvable difference"
except where noted below. Allocation figures are deterministic and are the more reliable signal in this run.

## Observations (as measured, including losses)

1. **Latency, small queries (one-row, scalar, typed-params, buffered-100):** all five implementations are within the
   noise of each other. A single-row round trip costs about 350–440 μs on this setup, dominated by the loopback
   database round trip and pool open/close. This run can't tell the libraries apart on latency.
2. **Allocations, small queries:** VelQuery allocates more than raw ADO.NET and Dapper.AOT in every small case.
   one-row: +1.19 KB (descriptor) / +1.52 KB (runtime SQL) over raw; Dapper.AOT +0.64 KB. Plain Dapper allocates
   the most in one-row and typed-params.
3. **Allocations, per row — VelQuery loses clearly.** buffered-1000: VelQuery 381–385 KB vs 233 KB for raw ADO.NET
   and Dapper.AOT (+64%). stream-100k: 36.6–37.3 MB vs 21.7 MB (+69%, about 150 B/row more). Plain Dapper is in
   between (29.3 MB; boxing in its emitted mapper).
4. **Latency, per row:** buffered-1000 VelQuery descriptor 1.26× raw (±0.08), runtime SQL 1.15×; stream-100k
   descriptor 1.29× (its error is ±1.3 ms, so this is the most stable number in the table), runtime SQL 1.21×
   (±25.6 ms). Dapper.AOT stream-100k was also slow (1.40×) but with a ±41 ms error, so treat it as noise.

### Likely cause of the per-row gap (investigated)

`alloc-profile-stream100k.txt` (produced with `--alloc-profile`, sampled GC AllocationTick events by type):

| Variant | B/row | `byte[]` B/row |
|---|---|---|
| raw-adonet, `SequentialAccess` | 228 | 3.8 |
| raw-adonet, `CommandBehavior.Default` | 318 | 141.8 |
| dapper-aot | 228 | 3.8 |
| velquery-descriptor | 388 | 136.9 |

VelQuery's executor opens readers with `CommandBehavior.Default`
(`src/VelQuery.Core/Execution/VelQuerySession.Execution.cs` `BufferAsync`, `src/VelQuery.Core/Execution/QueryStream.cs`
`MoveNextAsync`). In that mode SqlClient buffers each row into `byte[]` (about 140 B/row here; AllocationTick sampling is approximate) and its async
`ReadAsync` path allocates timeout timers and continuations. Raw ADO.NET using the same behavior allocates almost as
much as VelQuery. Dapper and Dapper.AOT use `SequentialAccess`. The remaining ~70 B/row between VelQuery and
"raw + Default" comes from VelQuery's per-row `MoveNextAsync` state machine boxes (`AsyncStateMachineBox` in the
profile, allocated whenever `ReadAsync` actually suspends), extra `Task<bool>`/continuation objects, and sampling
noise. The first run of this suite (below) used `CommandBehavior.Default` in the raw baseline too, and there raw
and VelQuery allocated the same (36.5 vs 37.2 MB for stream-100k). That supports this explanation.

`SequentialAccess` requires reading columns in ordinal order. That holds for generated mappers but not for
arbitrary hand-written or reflection mappers, so changing it is a design decision (ADR-0004 follow-up), not a free
switch. No production code was changed for this report.

## Earlier attempt on the same day (discarded, not overwritten silently)

A first ShortRun (raw baseline **without** `SequentialAccess`) was replaced by this one. In it,
`Stream100k.velquery-descriptor` **failed**: after two measured iterations of about 51 ms each, a `ReadAsync` call
raised `VelQueryTimeoutException` → `SqlException` -2 "Execution Timeout Expired" (SqlClient default 30 s command
timeout). Another session's SQL Edge container was starting on the same 2 GB VM at about that time. The failure did
not reproduce in this run (all 30 benchmarks succeeded), so the most likely cause is a server/VM stall. A VelQuery
streaming defect is not ruled out: it needs a repeated stream-100k soak on an uncontended SQL Server 2022 host.

## Reproduce

```bash
cd benchmarks/VelQuery.Benchmarks
DOCKER_HOST=unix://$HOME/.colima/default/docker.sock TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock \
  dotnet run -c Release -- --filter '*' --job short --artifacts ../results/<date>-<machine>-shortrun
dotnet run -c Release -- --alloc-profile   # allocation-by-type view for stream-100k
```
