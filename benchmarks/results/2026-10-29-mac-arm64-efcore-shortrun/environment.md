# 2026-10-29 — Apple M5 Pro — ShortRun with EF Core

**Low statistical confidence for time.** BenchmarkDotNet `ShortRun` (1 launch, 3 warmup, 3 measured iterations;
stream-100k: 2 warmup, 5 measured) on a laptop against a loopback Docker database in a 2-vCPU VM. Most time
differences below are far smaller than the reported error (half of the 99.9% confidence interval, shown as ±). Treat
time as "no measurable difference" unless a gap is larger than both errors. **Allocated bytes are exact and
repeatable**: they are the reliable result of this run.

## Environment

| Item | Value |
|---|---|
| Host | Apple M5 Pro, 15 cores, macOS 26.5.1, arm64 |
| Runtime | .NET 10.0.5 (Arm64 RyuJIT), SDK 10.0.201, workstation non-concurrent GC, Release |
| BenchmarkDotNet | 0.15.8, out-of-process toolchain, `MemoryDiagnoser` |
| Database | Azure SQL Edge 1.0.7 Developer (SQL Server engine 15.0.2000.1574, ARM64) via Testcontainers.MsSql 4.16.0. Not comparable with SQL Server 2022 results |
| Container runtime | Colima (Docker 29.2.1), Linux VM aarch64, 2 vCPU, 1.91 GB RAM, loopback TCP |
| Driver | Microsoft.Data.SqlClient 7.1.1 |
| Libraries | Dapper 2.1.89, Dapper.AOT 1.1.0, **EF Core 10.0.13** (Microsoft.EntityFrameworkCore.SqlServer), VelQuery 0.1.0-alpha (commit 08c230c) |
| EF Core setup | Pooled `DbContext` factory, `NoTracking`, compiled queries (`EF.CompileAsyncQuery`), explicit column types; EF generates its own SQL (for example `TOP(2)` for single-or-default) |
| Connection | `Pooling=true; Max Pool Size=100; MARS=false`; one pooled open/close per operation for every library |
| Data | `dbo.BenchRows`, 100,000 rows, seed 20261010 |
| Correctness | Before measuring, every library's result was compared with raw ADO.NET in every case (all equal) |

Raw machine record: [`environment.json`](environment.json). Full data: `results/*-report-full.json`.

## Results (mean ± error / allocated per operation)

| Case | raw-adonet | dapper | dapper-aot | velquery-descriptor | velquery-runtime-sql | efcore-compiled |
|---|---|---|---|---|---|---|
| one-row-projection | 359.6 μs ±151.5 / 6.10 KB | 354.9 μs ±110.6 / 7.86 KB | 357.8 μs ±222.4 / 6.75 KB | 355.8 μs ±137.4 / 6.81 KB | 354.0 μs ±36.4 / 7.14 KB | 355.5 μs ±100.3 / 8.81 KB |
| scalar-count | 400.5 μs ±66.1 / 4.12 KB | 397.6 μs ±220.0 / 4.27 KB | 398.7 μs ±83.0 / 4.38 KB | 411.8 μs ±160.2 / 4.47 KB | 399.0 μs ±25.1 / 4.50 KB | 406.4 μs ±240.4 / 5.73 KB |
| buffered-100 | 466.6 μs ±53.3 / 27.78 KB | 481.5 μs ±207.0 / 37.15 KB | 482.5 μs ±149.2 / 28.56 KB | 453.6 μs ±46.0 / 27.13 KB | 478.4 μs ±166.7 / 27.17 KB | 498.6 μs ±183.2 / 58.46 KB |
| buffered-1000 | 1,239 μs ±2,321 / 232.75 KB | 1,354 μs ±885 / 312.43 KB | 1,277 μs ±776 / 233.76 KB | 1,290 μs ±1,930 / 225.12 KB | 1,270 μs ±454 / 225.34 KB | 1,765 μs ±3,871 / 569.33 KB |
| stream-100k | 37.0 ms ±1.2 / 21.64 MB | 37.8 ms ±1.1 / 29.32 MB | 45.0 ms ±23.8 / 21.70 MB | 45.7 ms ±27.3 / 21.68 MB | 51.7 ms ±3.0 / 21.64 MB | 45.7 ms ±26.4 / 55.38 MB |
| typed-params | 343.0 μs ±135.0 / 7.64 KB | 346.6 μs ±5.4 / 10.44 KB | 348.2 μs ±73.4 / 8.29 KB | 347.9 μs ±116.9 / 8.54 KB | 348.7 μs ±48.3 / 9.30 KB | 359.7 μs ±53.5 / 10.91 KB |

## Reading

- **Memory:** EF Core (compiled, no tracking) allocates 1.4–2.6× raw ADO.NET (2.5× for 1,000 buffered rows, 2.6×
  for a 100k-row stream). VelQuery allocates 0.97–1.17× raw ADO.NET and less than Dapper in every case.
- **Time:** apart from the one gap below, no difference above the error in this run, for any library. The database round trip dominates the
  small cases; the large cases are too noisy here. A full (default job) run on an x64 SQL Server 2022 host is needed
  before any time comparison with EF Core is quoted.
- velquery-runtime-sql in stream-100k (51.7 ms ±3.0 against 37.0 ms ±1.2 for raw ADO.NET) is the one gap larger than
  both errors. The 2026-10-10 run showed the same pattern (49.6–53.1 ms against 41.3 ms), while the CPU profile
  (`--cpu-profile`) measures VelQuery at 0.96–1.05× raw ADO.NET CPU for the same stream, so the gap is time spent
  waiting, not extra work. It stays an open item: a repeated stream-100k run on an uncontended SQL Server 2022 host.
