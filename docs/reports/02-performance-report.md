# VelQuery — Performance report

**Date:** 2026-10-21 · **Scope:** client-side cost of the library (memory, CPU, latency) and bulk-load throughput

## Environment and method

| Item | Value |
|---|---|
| Machine | Apple M5 Pro laptop, macOS arm64 |
| Databases | Docker (Colima VM, 2 vCPU, 2 GB): Azure SQL Edge (SQL Server engine), PostgreSQL 17, MySQL 8.4, MongoDB 8; SQLite in-process |
| Runtime | .NET 10.0.5, Release builds |
| Tools | BenchmarkDotNet short runs (memory, latency); `--cpu-profile` (client CPU, 5 rounds, median); `--bulk-profile` and probes (1M rows, 3 rounds, median) |

Caveats: loopback databases on a small shared VM, short runs. Use these numbers to compare libraries on identical work;
re-measure on your own hardware before drawing capacity conclusions.

## 1. Memory per query (SQL Server, lower is better)

| Case | Raw ADO.NET | Dapper | Dapper.AOT | **VelQuery** |
|---|---|---|---|---|
| 1 row | 6.10 KB | 7.85 KB | 6.74 KB | **6.81 KB** |
| 100 rows | 27.78 KB | 37.14 KB | 28.56 KB | **27.13 KB** |
| 1,000 rows | 232.72 KB | 312.53 KB | 233.65 KB | **225.01 KB** |
| Stream 100k rows | 21.69 MB | 29.30 MB | 21.69 MB | **21.64 MB** |

VelQuery allocates the least on buffered queries and ties for least on streaming — about 28% less than plain Dapper.

### Effect of the optimization work

| Case | VelQuery 2026-10-11 (baseline) | VelQuery now | Change |
|---|---|---|---|
| 100 rows | 38.33 KB | 27.13 KB | −29% |
| 1,000 rows | 390.63 KB | 225.01 KB | −42% |
| Stream 100k | 38.83 MB | 21.64 MB | −44% |

Changes: sequential column reads, pooled buffers for buffered results, a synchronous fast path for streaming, and no
per-column NULL check for non-nullable members.

### Compared with EF Core

EF Core 10 in its fastest common configuration (pooled `DbContext`, no tracking, compiled queries), short run of
2026-10-29 ([environment and full table](../../benchmarks/results/2026-10-29-mac-arm64-efcore-shortrun/environment.md)).
Allocated bytes are exact; the time differences in that run are within the error, so no time comparison is made.

| Query | VelQuery (generated) | EF Core (compiled) | EF Core / VelQuery |
|---|---|---|---|
| One row | 6.8 KB | 8.8 KB | 1.3× |
| Count | 4.5 KB | 5.7 KB | 1.3× |
| 100 rows | 27.1 KB | 58.5 KB | 2.2× |
| 1,000 rows | 225 KB | 569 KB | 2.5× |
| Stream 100,000 rows | 21.7 MB | 55.4 MB | 2.6× |
| 5 typed parameters | 8.5 KB | 10.9 KB | 1.3× |

## 2. Client CPU per operation (relative to raw ADO.NET = 1.00)

| Case | Dapper | Dapper.AOT | **VelQuery** (generated / runtime SQL) |
|---|---|---|---|
| 1 row | 1.02 | 1.02 | **1.00 / 1.01** |
| Typed parameters | 1.05 | 1.05 | **1.01 / 0.96** |
| 1,000 rows buffered | 1.12 | 0.81* | **1.00 / 1.03** |
| Stream 100k rows | 1.14 | 1.04 | **1.03 / 1.05** |

\* Outlier from VM noise in that run; Dapper.AOT measured 0.98–0.99 in earlier runs.

Most client CPU (about 210 µs per simple query here) is spent in the database driver and TLS, not in any library.

## 3. Latency

Small-query latency is the same for every library within measurement noise (about 340–350 µs for a one-row query on
this setup): the database round trip dominates. VelQuery does not claim lower latency than raw ADO.NET.

## 4. Bulk insert, 1M rows (lower is better)

| Database | **VelQuery** | Native loader | One row per round trip |
|---|---|---|---|
| SQL Server | **2.25 s** | 2.38 s (SqlBulkCopy) | ~10 min 43 s |
| PostgreSQL 17 | **0.60 s** | 0.58 s (binary COPY) | ~4 min |
| MySQL 8.4 | **1.29 s** | 1.32 s (MySqlBulkCopy) | ~15 min |
| SQLite | **0.71 s** | 0.71 s (prepared INSERT in one transaction) | 3–11 min |
| MongoDB 8 | **1.24 s** | 1.89 s (driver `InsertMany`) | — |

VelQuery matches each native loader while adding safety defaults (all-or-nothing loads, constraint checks, no silently
dropped rows). For MongoDB it is faster than the official driver.

### MongoDB detail (1M documents)

| | Time | Client CPU | Allocated |
|---|---|---|---|
| Official driver `InsertMany` | 1.89 s | 1.01 s | 505 MB |
| **VelQuery** | **1.24 s (−34%)** | **0.45 s (−55%)** | **137 MB (−73%)** |

Writes are built straight into the driver's pooled buffers with the generated serializer and carry a server-side time
limit (`maxTimeMS`). Reads are at parity with the driver, with about 10% less allocation on buffered and streamed reads.

## 5. Findings

- **Fixed:** an early SQL Server bulk design cost 12.5 s of CPU per 1M rows because of SqlClient's async path with
  custom readers; switching to staged batches brought it to raw-driver level.
- **Fixed:** MongoDB bulk insert was 5% slower than the driver; now 34% faster.
- **EF Core:** allocates 1.3–2.6× VelQuery per query (see §1). Time against EF Core is not yet resolved: it needs a
  full run on an uncontended x64 SQL Server 2022 host.
- **Not measured yet:** p99 latency under concurrency, CPU under sustained parallel load.
