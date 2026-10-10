# Provider compatibility

Tracks what VelQuery supports per database provider, what has been verified, and on which exact versions (PRD §10,
§29 item 7). **A feature is "verified" only when an automated test on the listed driver/server/OS passed.** Code that
exists but has no passing test on a listed environment is "implemented, not yet verified". Nothing here is a support
claim for an untested combination (PRD §10.1 "important qualification").

Last updated: 2026-10-10 (VelQuery 0.1.0-alpha, pre-Gate B).

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
| Other types (Guid, DateTimeOffset, binary, enums, DateOnly/TimeOnly) | VQ-FR-014 | Implemented | `DateOnly`/`TimeOnly` mapping has a known gap (ADR-0004 open question) |
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

## PostgreSQL (wave 2) — `VelQuery.PostgreSQL`, Npgsql

| Capability | Status |
|---|---|
| All capabilities | Not started (PRD D-04: shipped as supported only if parity gates pass) |
| Native AOT (any RID / auth) | Not started |

## How to update this file

1. Add or update an environment row with exact server build, driver version, runtime and OS/RID.
2. Change a capability to "Verified" only with a link to the test (or CI job) that passed on that environment.
3. Record failures and exclusions explicitly; never delete a failed combination to make the table look green.
