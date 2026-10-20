# VelQuery — Quality, testing and security report

**Date:** 2026-10-21

## 1. Test inventory and results

| Suite | Tests | Result | Runs against |
|---|---|---|---|
| Core unit tests | 537 | Pass | In-memory fake ADO.NET driver with fault injection |
| SQL Server integration | 125 | Pass | Azure SQL Edge (SQL Server engine) in Docker |
| MongoDB generator / diagnostics | 104 | Pass | Roslyn compilations |
| SQL analyzers and dialect scanner | 90 | Pass | Roslyn compilations |
| MongoDB integration | 85 | Pass | MongoDB 8 replica set in Docker |
| SQLite integration | 71 | Pass | SQLite files (no Docker) |
| MySQL integration | 58 | Pass | MySQL 8.4 and MariaDB 11.8 |
| PostgreSQL integration | 31 | Pass | PostgreSQL 17 |
| Generator runtime | 23 | Pass | Generated code over in-memory readers |
| Generator golden (snapshot) | 12 | Pass | Checked-in expected output, determinism, incremental caching |
| Native AOT smoke | 1 binary | Pass | SQLite, PostgreSQL, MySQL (native executable) |
| **Total** | **1,136** | **All pass** | Locally, one suite at a time |

What the suites cover: result cardinality rules, type round-trips, injection attempts, timeouts versus cancellation,
streaming with early exit, connection-pool leaks after faults, caller-owned versus library-owned transactions, bulk
load failure semantics, strict NULL mapping, and build-time diagnostics at exact source locations.

## 2. Defects found and fixed during development

| Defect | Found by | Impact if shipped |
|---|---|---|
| Reader disposed twice when its dispose threw | Unit tests | Breaks dispose-exactly-once rule |
| Cleanup failure escaped as a raw driver exception | Unit tests | Broke the error contract |
| A refactor skipped command disposal after an earlier dispose failed | Unit tests | Resource leak |
| DI scope crashed on synchronous dispose | Benchmark agent review | Crash in some hosts |
| `VelQuery.MongoDB` package missing its source generator | Packaging check | Package unusable from NuGet |
| Strict NULL detection failed on Npgsql in sequential mode | PostgreSQL tests | Raw cast error instead of mapping error |
| MySQL bulk load silently skipped bad rows | MySQL tests | Silent data loss |
| SQLite ignored cancellation during long statements | SQLite tests | Unstoppable queries |
| Build-time scanner raised false warnings on MySQL/SQLite/PostgreSQL syntax | Dialect tests | Noise, missed checks |
| Capability flags declared but never enforced | Dead-code review | Misleading guarantees |

## 3. Code quality controls

- Warnings are errors in every project, including dead-code rules (unused usings, members, assignments, fields).
- Nullable reference types, XML documentation on every public API, AOT and trimming analyzers on all libraries.
- Central package versions with lock files; locked restore verified.
- Deterministic builds; generator output stable byte-for-byte (golden tests).

## 4. Security posture

| Control | Status |
|---|---|
| Parameter values always bound, never concatenated | Enforced; VQ1008 rejects string-built SQL; injection tests on every SQL provider |
| MongoDB operator injection | Placeholders allowed only in value positions (VQ2006–VQ2008); `$literal` wrapping; tested with operator-shaped inputs |
| Errors never contain parameter values, row values or connection strings | Enforced; canary-value tests |
| Known vulnerable dependencies | None. CVE-2026-84008 patched by pinning System.Security.Cryptography.Pkcs 9.0.21 |
| Dependency updates | Dependabot configured (not yet active) |
| Vulnerability reporting | SECURITY.md (private reporting) |

## 5. Native AOT

| Database | Status | Reason |
|---|---|---|
| SQLite, PostgreSQL, MySQL | Verified (osx-arm64) | Zero trim/AOT warnings; smoke binary passes |
| SQL Server | Blocked | Microsoft.Data.SqlClient 7.1.1 is not AOT-compatible |
| MongoDB | Not supported | MongoDB.Driver is not AOT-safe |

## 6. Known limitations

- No CI run yet; every result above is from one developer machine.
- SQL Server tested on Azure SQL Edge, not SQL Server 2022 on x64.
- MongoDB bulk insert is not all-or-nothing outside a transaction (MongoDB behavior; documented and tested).
- MySQL bulk insert needs `local_infile` enabled on client and server.
- SQLite stores dates/decimals as text or real; precision follows SQLite's storage rules (STRICT tables recommended).
