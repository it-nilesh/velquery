# VelQuery — Product Requirements Document

**Ultra-Fast SQL Query Engine • Compile-Time Safety • AI-Native Query Intelligence**  
**Version:** 1.0 (implementation baseline) | **Date:** 10 October 2026 | **Status:** Proposed / Ready for engineering kickoff  
**Product:** VelQuery | **Initial product:** VelQuery Query Engine | **Audience:** Product Owner, Principal Architect, Engineering, QA, DevEx, Security, Developer Relations  
**Document ownership:** Product + Architecture | **Decision status:** Requirements and proposed architecture; performance and compatibility targets require validation

> **North-star product statement:** VelQuery makes hand-written SQL safer to ship, faster to execute, easier to diagnose, and easier to optimize—without making every database request depend on AI, a cloud service, reflection-heavy mapping, or a heavyweight ORM.

> **Important:** This PRD defines planned behavior, interfaces, quality gates, and release criteria. It does **not** assert that VelQuery currently exists or has already beaten Dapper, Dapper.AOT, EF Core, RepoDB, LINQ to DB, or raw ADO.NET. All example APIs and benchmarks are specifications, not observed results.

---

## 0. Executive decision record

| Decision | Baseline |
|---|---|
| Product category | High-performance .NET SQL query engine with optional, out-of-band AI intelligence |
| Product wedge | **Query-first**: execute, map, stream, validate, observe; not a full ORM at launch |
| Primary target | .NET 10 LTS / C# 14; initially SQL Server; PostgreSQL follows behind parity gates |
| Core technology | ADO.NET provider drivers + Roslyn incremental generator + analyzers |
| Runtime execution | Direct generated command and row materialization; dynamic fallback must be explicit |
| Runtime dependencies | No SaaS, AI model, telemetry service, or license server dependency for query execution |
| AI boundary | Control/developer plane, not synchronous request hot path |
| Security | Parameterization, strict opt-in unsafe SQL, least privilege, data-minimizing telemetry |
| Commercial split | Apache-2.0 proposed for Core; paid Studio, Cloud, and Enterprise add capabilities, not execution speed |
| Release quality | Verified correctness, repeatable benchmarks, provider compatibility, AOT evidence, failure-injection tests |
| Initial exclusions | Change tracking, full LINQ ORM, migrations, sharding, automatic index changes, auto-running AI SQL |

### Release vocabulary

- **MVP / v0.1:** usable query API with correctness, core async commands, SQL Server tests, benchmarks.
- **v0.5:** generated fast path, robust mapping, streaming, multi-result, provider-focused tuning.
- **v1.0 Core GA:** documented production quality, query correctness and diagnostics, provider matrix, reproducible benchmark publication, security gates.
- **v1.5 AI Studio:** optional offline/asynchronous AI SQL advice and developer workflow.
- **v2.0 Platform:** further Engine 3–6 modules: advanced data services, team governance, SaaS intelligence.

The v1.0 label in this document is the **PRD revision**, not a claim that software v1.0 has shipped.

## 1. Market problem, present-day friction, and differentiation

### 1.1 Problems observed in the SQL application lifecycle

| Problem ID | Developer / business pain | What VelQuery should do | Success evidence |
|---|---|---|---|
| PR-01 | ORM abstraction hides costly SQL, overfetching, inefficient joins, and round trips | Provide SQL-first execution with transparent query identity and optional plan analysis | Lower unexplained query regressions in pilots |
| PR-02 | Hand-written SQL compiles as C# while columns, parameter names, and types are wrong | Schema-backed analyzer and source-generated typed contracts | Build/CI catches supported mismatch cases |
| PR-03 | Reflection and generic runtime mapping can add overhead in short queries | Generate binders/materializers at compile time | Reproducible allocation and throughput results |
| PR-04 | Large results become costly when libraries buffer everything | True async streaming with deterministic resource release | Memory remains bounded for streamed rows |
| PR-05 | Databases see implicit conversions and unstable plans from poorly typed parameters | Provider-native parameter metadata and diagnostics | Tests verify SQL types, lengths, scale, precision |
| PR-06 | Database errors are difficult to reproduce or trace to source | Stable query IDs, correlation, structured exceptions, opt-in safe diagnostics | Time-to-diagnose reduction in pilots |
| PR-07 | Query performance regresses after code and schema deployments | Baseline comparisons, plan fingerprinting, CI query budgets | Regression found before release |
| PR-08 | AI-generated SQL is plausible but can be wrong, expensive, or unsafe | Bounded read-only evaluation, schema validation, tests, human approval | No autonomous production data modifications |
| PR-09 | Native AOT and trimming are difficult across providers and libraries | Generated execution + explicit compatibility matrix + automated publish smoke tests | Zero linker warnings on supported, documented paths |
| PR-10 | Team-level governance is fragmented | Future policy-as-code and approval/evidence integration; core still independent | Enterprise pilot validates governance workflows |

**Sources and caveats.** EF Core itself documents performance trade-offs in query compilation and multi-collection loading; Dapper.AOT already uses build-time generation. Neither limitation is proof that VelQuery will be faster in every case. SQL injection prevention and AI-tool least privilege follow OWASP guidance. References are collected in Section 28.

### 1.2 Positioning

**Against Dapper / Dapper.AOT:** prioritize type/schema validation, first-class diagnostics and codegen workflow without losing a low-overhead SQL path; benchmark before claiming advantage. **Against EF Core:** keep SQL explicit and tracking absent from Core; do not compete on full aggregate persistence at launch. **Against RepoDB / LINQ to DB:** do not chase breadth first; win on compilation safety, simple API, verifiable performance, and developer feedback. **Against database IDEs:** connect query source code, deployment regression evidence, and optional AI recommendations.

### 1.3 User personas and jobs to be done

| Persona | Job to be done | Top requirement |
|---|---|---|
| High-throughput API developer | Make frequent SQL calls with predictable latency and low allocations | Simple fast query API; async correctness |
| Senior backend / platform engineer | Standardize safe SQL access across teams | Stable contracts, analyzers, provider policies, CI |
| SRE / performance engineer | Identify why a query slowed after a release | Query identity, sampled timing, execution-plan evidence |
| SQL / database engineer | Evaluate index/query rewrites without production risk | Plan comparison, controlled replay, cost trade-offs |
| Security engineer | Prevent unsafe query generation and data disclosure | Least-privilege evaluation, redaction, auditability |
| Developer using coding agents | Convert AI-generated suggestions into correct, testable code | Schema-bound checks, reviewable diffs, generated tests |
| Engineering manager / buyer | Reduce SQL incidents and developer debugging time | Adoption, regression and MTTR metrics; commercial support |

## 2. Product goals, outcomes, and explicit non-goals

### 2.1 Strategic goals

G-01. Achieve excellent SQL-call overhead without sacrificing exact result semantics, transaction integrity, cancellation, or resource safety.  
G-02. Shift common SQL mistakes left with Roslyn analyzers and offline schema snapshots.  
G-03. Make performance observable through low-cardinality query identities, privacy-by-default instrumentation, and repeatable benchmarks.  
G-04. Let future AI recommend query improvements, but **never** insert an LLM into the synchronous database hot path.  
G-05. Win adoption through a pleasant, familiar API, stable packages, simple documentation, and a low-friction Dapper migration path.

### 2.2 Measurable launch success criteria (targets, not claims)

| Metric | v1.0 proposed acceptance threshold | Method |
|---|---|---|
| Query result correctness | 100% pass of defined semantics tests and provider matrix | SQL Server + PostgreSQL designated CI matrix |
| Generated path reflection | No application-level reflection during generated bind/map calls | Source inspection + profiler + AOT tests |
| Simple query overhead | Target <= 1.05× latency of optimized raw ADO.NET in selected micro/integration baselines | Same driver/query/database/network; report confidence intervals |
| Dapper.AOT competitiveness | Match or improve on at least 3 *specified* representative hot-path cases; publish any losses | BenchmarkDotNet; no selective reporting |
| Streaming memory | Bounded additional in-library memory as row count rises; no full-result buffering | 10K, 100K, 1M rows; allocation and retained heap |
| Native AOT | Publish and run documented supported provider/features with warnings-as-errors | linux-x64 + win-x64 smoke matrix; exceptions noted |
| Build analyzer UX | < 2 seconds incremental overhead on medium sample solution as an initial engineering budget | Cold/warm incremental build measurement |
| Telemetry off | No activity creation, SQL parsing, or AI work on disabled fast path | Instrumentation benchmark |
| Sensitive telemetry | Zero parameter values/credentials emitted by default | Automated log/trace leak tests |
| Release safety | No open critical security bugs, no unresolved data-corruption bugs | Signoff checklist and incident simulations |
| Adoption discovery | 5 design-partner teams; target 1 commercially willing pilot | Qualitative and usage research, not assumed revenue |

Do not use an end-to-end *query* SLA as a library guarantee: database CPU, locks, plans, I/O, round trips and connection pool pressure often dominate request time. The release must report raw results, environments, variance, and significant regressions.

### 2.3 Non-goals of query-first Core GA

Full LINQ-to-SQL translation, EF-style change tracking, lazy-loading/navigation graphs, migrations, bulk data frameworks, automatic sharding, automatic read-replica selection, result-set distributed caching, schema mutation, universal SQL parsing, cloud agents that execute against production, all DB providers simultaneously, and any mandatory hosted control plane. All are separately prioritized or explicitly deferred.

## 3. Six-engine product architecture: now versus later

| Engine | Purpose | Query-first Core GA | Subsequent evolution |
|---|---|---|---|
| **E1. Execution Engine** | Parameter binding, command execution, typed mapping, async, streaming, transactions | **Ship** | Native batching, provider-optimized bulk |
| **E2. Query Compiler** | Static SQL discovery, source generation, offline schema checks, diagnostics | **Ship minimal** | Richer SQL dialect reasoning, safe query rewrite suggestions |
| **E3. Data Engine** | Fluent querying, CRUD, bulk, optional tracking, migrations | Only extension seam, **not** full engine | Independently installed modules |
| **E4. AI Intelligence** | SQL optimization, plan analysis, index advice, performance investigation | Design contracts and safe metadata formats; no LLM at runtime | Studio agent workflows, controlled evaluation |
| **E5. Governance** | Security policies, tenant context, approvals, audit evidence | Core deterministic safety rules; no cloud policy dependency | Enterprise policy engine and evidence packs |
| **E6. Developer Platform** | CLI, IDE, benchmark reporting, CI checks, Studio/Cloud | Docs, samples, CLI validation and CI integration | Paid Studio, Cloud and private deployment |

**Architectural rule:** E1 is a standalone leaf-dependency runtime. E2 operates at build time. E4–E6 use E1's published contracts and telemetry rather than imposing synchronous requirements on E1. E3 is optional and must not make E1 reference a tracking/ORM package.

## 4. High-level architecture and communication

```text
                  SOURCE / BUILD / CONTROL PLANE (no request hot path)
  ┌────────────────┐   ┌──────────────────┐   ┌───────────────────────────┐
  │ .cs + SQL text │──>│ Roslyn analyzer  │──>│ typed binder/materializer│
  │ schema snapshot│   │ SQL validation   │   │ generated C# + diagnostics│
  └────────────────┘   └──────────────────┘   └───────────────────────────┘
                                                            │ compiled into app
                                                            ▼
                 APPLICATION PROCESS / REQUEST HOT PATH
  ┌────────────────────┐  ┌─────────────────────────────────────┐
  │ ASP.NET / workers  │─>│ VelQuery.E1 public query API         │
  └────────────────────┘  │ generated descriptor → bind → exec   │
                          │ → read → materialize → dispose       │
                          └──────────────────┬──────────────────┘
                                             ▼
                                  ADO.NET provider adapter
                                             ▼
                                  SQL Server / PostgreSQL
                                             │
                 OPTIONAL OBSERVATION (off by default)           
                  bounded safe summary + metrics
                                             ▼
                          OTel / offline analysis / Studio
                                             ▼
                         AI recommendations (human review)
```

### 4.1 Component responsibilities

- **VelQuery.Core:** public API, state machine, execution coordinator, cancellation/timeout plumbing, result contracts, ownership contracts, error taxonomy. No dependency on AI, UI or Cloud.
- **VelQuery.Abstractions:** lightweight contracts for generated commands, mapper, binder and provider capabilities; backward-compatible interfaces.
- **VelQuery.Generators:** incremental compilation of eligible static query shapes into strongly typed binders/materializers.
- **VelQuery.Analyzers:** security rules, missing/mismatched parameters, unsupported expressions, generated-path/validation diagnostics.
- **VelQuery.SqlServer:** T-SQL parameter metadata, types, prepared strategy and dialect semantics built on Microsoft.Data.SqlClient.
- **VelQuery.PostgreSQL:** PostgreSQL types, parameter mapping and prepared strategy built on Npgsql; parity gated separately.
- **VelQuery.Diagnostics:** optional Activity/Meter integrations, safe query identifiers, sampling and bounded sinks.
- **VelQuery.Schema:** offline schema snapshot schema and validation tooling; no production DB introspection in build agents by default.
- **VelQuery.Benchmarks / Tests:** truthful comparisons, integration correctness, AOT executable smoke projects.
- **Future:** VelQuery.AI, Studio, Cloud, Enterprise packages consume explicit public, versioned contracts.

### 4.2 Trust and data boundaries

1. Runtime reads configuration/DB credentials from application-owned secret sources; VelQuery itself does not store credentials.
2. Compile-time schema snapshot is a reviewed, versioned artifact containing only allowed metadata; secrets and data rows are forbidden.
3. Telemetry goes to the application's configured sink; query IDs/operation and durations are default, SQL text only after redaction and opt-in.
4. AI sees allowlisted schema metadata, plans and aggregate performance facts; raw parameter values and customer data are excluded by default.
5. The AI model never receives a production write-capable database connection through the VelQuery platform.

## 5. Product requirements conventions

**Priority:** P0 = Core GA blocker, P1 = fast follow after Core GA or conditional if capacity permits, P2 = roadmap.  
**Acceptance notation:** Every requirement uses externally testable **Given / When / Then** semantics; ambiguities default to safe/explicit behavior and require an ADR before public API freeze.  
**Requirement IDs:** VQ-FR = functional; VQ-NFR = nonfunctional; VQ-AI = AI; VQ-SEC = security; VQ-REL = reliability; VQ-DX = developer experience.

## 6. P0 functional requirements — query execution engine

| ID | Capability | Required behavior / acceptance test | P |
|---|---|---|---|
| VQ-FR-001 | Connection ownership | Given a caller-owned open `DbConnection`, execution **never disposes it**; given a factory-owned connection, VelQuery opens/disposes it even on errors | P0 |
| VQ-FR-002 | Query buffered | `QueryAsync<T>` returns `IReadOnlyList<T>`; empty query returns empty list; mapped order equals reader order | P0 |
| VQ-FR-003 | First semantics | `QueryFirstAsync<T>` returns first or throws `VelQueryNoRowsException`; `QueryFirstOrDefaultAsync<T>` returns default on zero | P0 |
| VQ-FR-004 | Single semantics | `QuerySingleAsync<T>` requires exactly one row; 0 and >1 throw distinct typed exceptions; zero ambiguity with null is documented | P0 |
| VQ-FR-005 | Scalar semantics | `ExecuteScalarAsync<T>` handles null/DBNull with strict nullable conversion; conversion failures are actionable | P0 |
| VQ-FR-006 | Non-query execution | `ExecuteAsync` returns provider-reported affected row count; no invented count and no hidden retries | P0 |
| VQ-FR-007 | Parameter binding | Map named parameters to strongly typed `DbParameter`s; preserve database null, avoid interpolating values in SQL | P0 |
| VQ-FR-008 | Parameter metadata | Allow DbType, size, precision, scale, direction, and provider-specific extensions; tests demonstrate no wrong implicit type selection | P0 |
| VQ-FR-009 | Cancellation | Every async API accepts `CancellationToken`; cancellation is forwarded to provider; command/reader/owned connection disposed | P0 |
| VQ-FR-010 | Command timeout | Per-call timeout overrides scoped defaults; zero/infinite semantics explicit and provider-aware; no hidden global mutable switch | P0 |
| VQ-FR-011 | Explicit transactions | Accept caller-owned `DbTransaction`; command binds it; VelQuery never commits or rolls back caller-owned transaction | P0 |
| VQ-FR-012 | Transaction convenience | Optional scoped transaction helper uses explicit commit and guaranteed rollback/dispose on incomplete scope | P0 |
| VQ-FR-013 | Async streaming | `StreamAsync<T>` reads one row at a time; `await foreach` early exit/cancel disposes reader/command and owned connection | P0 |
| VQ-FR-014 | Materialization | Map primitive, nullable, enum, Guid, DateTime, DateTimeOffset, decimal, string, byte[], records/constructors where statically resolvable | P0 |
| VQ-FR-015 | Alias support | Column-to-member mapping respects explicit `[Column]`/mapping attributes and documented name strategy; duplicate aliases rejected if ambiguous | P0 |
| VQ-FR-016 | Nullability | Database NULL to non-nullable target triggers configurable strict error; strict by default in generated contracts | P0 |
| VQ-FR-017 | Column mismatch | Missing required columns/unsupported conversion yields clear query ID, column and expected type; no sensitive values in exception | P0 |
| VQ-FR-018 | Generated binder | Eligible calls generate direct parameter setup; no reflection/dynamic IL emission on generated execution path | P0 |
| VQ-FR-019 | Generated mapper | Eligible result types generate reader ordinal resolution and typed getters with schema verification | P0 |
| VQ-FR-020 | Fallback behavior | Unrecognized runtime query shapes fail with actionable diagnostics in strict/AOT mode; explicit compatibility fallback in JIT mode only | P0 |
| VQ-FR-021 | Multi-result | `QueryMultipleAsync` offers ordered result consumption; disposing before all results releases resources; incorrect result sequence errors clearly | P0 |
| VQ-FR-022 | SQL parameters attack resistance | Bound values cannot alter SQL structure; identifiers can only be inserted through explicit allowlisted APIs | P0 |
| VQ-FR-023 | Stored procedures | CommandType.StoredProcedure with parameter direction/output; provider-specific support matrix published | P0 |
| VQ-FR-024 | Supported provider behavior | SQL Server on .NET 10 baseline CI; Npgsql integration as staged parity gate; unsupported provider feature fails predictably | P0 |
| VQ-FR-025 | Query identity | Stable declared query name/ID included in diagnostics; no parameter values in emitted identifiers | P0 |
| VQ-FR-026 | Failure contract | Distinguish mapping, validation, timeout, cancellation, provider failures without losing original provider exception | P0 |
| VQ-FR-027 | Safe lifecycle | Command, reader and owned connection disposed on success, exception, partial enumeration, cancellation and consumer errors | P0 |
| VQ-FR-028 | Thread safety | Immutable query descriptors shared safely across threads; per-invocation mutable state not shared | P0 |
| VQ-FR-029 | Culture invariance | Numeric/date conversions use typed provider values and invariant semantics, not ambient locale string parsing | P0 |
| VQ-FR-030 | No implicit execution | Constructing query objects, generating code, or validating SQL must not execute SQL against production DBs | P0 |

### 6.1 P0 behavioral clarifications

- Query APIs are **logical operations**; VelQuery does not automatically retry writes, transactions, or streaming commands. Resilience extensions may be added later and must require explicit idempotency policy.
- A factory-owned connection is opened only when needed and closed after materialization (buffered) or enumeration completion (streamed). A caller-owned connection remains open; caller must keep it alive during streaming.
- `QuerySingleAsync` must check for a second row before completing. `QueryFirstAsync` need not read all remaining rows, but still releases the reader.
- Streaming returns rows as soon as the driver yields them; VelQuery cannot promise provider/network zero-buffering and must document driver behaviors.
- A shared mutable `DbConnection` is **not** automatically thread-safe. Independent requests use independent connections/data sources; no global lock workaround.
- When nullable semantics cannot be inferred reliably from compiler annotations and schema, validation warns rather than silently transforming values.

## 7. P0 / P1 compile-time query correctness

| ID | Requirement | Acceptance criteria | Priority |
|---|---|---|---|
| VQ-FR-031 | Static query capture | `[VelQuery]` attributed constant SQL or `.sql` file is discoverable by generator; dynamic SQL is not falsely validated | P0 |
| VQ-FR-032 | Generated code reproducibility | Same inputs, schema snapshot, compiler options => byte-for-byte stable generated source; no timestamps/random ordering | P0 |
| VQ-FR-033 | SQL parameter analyzer | Missing, extra and incompatible named parameters produce location-aware diagnostics for recognized SQL dialects | P0 |
| VQ-FR-034 | SQL parse safety | Invalid syntax in supported static subset yields diagnostic, not compiler crash; unsupported grammar marked unsupported, not safe | P0 |
| VQ-FR-035 | Build isolation | Generator/analyzer must not contact DB/network or read secrets during compilation | P0 |
| VQ-FR-036 | Schema snapshots | Versioned, deterministic snapshot format includes dialect, schema version/hash, tables, columns, nullability and typed metadata | P1 |
| VQ-FR-037 | Schema/type validation | Validate selected columns and result contracts against supplied matching snapshot; mismatch blocks strict CI | P1 |
| VQ-FR-038 | Query diagnostics | Analyzer IDs with stable severity/fix suggestions; suppressions require explicit justification for security categories | P1 |
| VQ-FR-039 | Schema drift CI | Compare approved snapshot with target integration schema; classify breaking vs nonbreaking changes | P1 |
| VQ-FR-040 | Prepared commands | Provider-aware `Prepare` policy benchmarked; no broad, unbounded global statement cache | P1 |
| VQ-FR-041 | Query plan evidence | On-demand local/test environment EXPLAIN/showplan analyzer with read-only role; not invoked during regular requests | P1 |
| VQ-FR-042 | Safe identifier API | Quote validated identifiers with dialect adapter; raw untrusted table/column identifier interpolation rejected | P1 |
| VQ-FR-043 | Diagnostics hooks | Optional low-overhead activity/metrics, query ID, durations, sanitized error category | P1 |
| VQ-FR-044 | CI performance baseline | JSON benchmark artifacts and comparison tool; release fails on confirmed serious regressions | P1 |
| VQ-FR-045 | Dapper migration guide | Equivalent examples for core Dapper query calls, lifecycle differences, and limitations | P1 |

### 7.1 Proposed compiler diagnostics

| Code | Default severity | Trigger | Example remediation |
|---|---|---|---|
| VQ1001 | Error | Static SQL references parameter not defined in bound contract | Add typed parameter or fix spelling |
| VQ1002 | Warning | Unused supplied parameter | Remove parameter or explicitly suppress |
| VQ1003 | Error | Generated mapper has unsupported target constructor/type | Use mapped record/type handler |
| VQ1004 | Error | Column-to-member mapping ambiguous | Alias columns or mark explicit mapping |
| VQ1005 | Warning/Error strict | SQL or selected column not in matching snapshot | Refresh snapshot or fix schema drift |
| VQ1006 | Warning | Inferred string/decimal metadata may cause type/plan issues | Specify length/precision/scale |
| VQ1007 | Error strict | AOT-required call would enter dynamic fallback | Register static query or opt out of strict AOT |
| VQ1008 | Error | Untrusted SQL identifier interpolation | Use allowlisted identifier contract |
| VQ1009 | Warning | Query cancellation/timeout not supplied in a configured sensitive context | Pass token/deadline |
| VQ1010 | Warning | Multiple-result contract does not match expected shape | Fix declared result ordering |
| VQ1011 | Info | Unsupported SQL dialect grammar prevents static checking | Validation unavailable; integration test required |

**Rule:** Never claim full SQL correctness from source analysis alone. Schema snapshots may be stale, databases have session and permission semantics, and dynamic SQL/stored procedures require integration tests.

## 8. Proposed public API and precise semantics

The API below is **normative intent** for review, not a compiled SDK. Freeze the public names only after a short spike and API usability tests. Avoid conflicts with existing Dapper extension method names by preferring a session object.

```csharp
// Application wiring: the caller owns the driver-specific DataSource/connection factory.
services.AddVelQuery(options =>
{
    options.DefaultTimeout = TimeSpan.FromSeconds(15);
    options.StrictMapping = true;
    options.AllowDynamicFallback = false; // recommended for AOT/reproducibility
});
services.AddVelQuerySqlServer(connectionFactory);

public sealed record UserRow(long Id, string Name, string? Email);

// Static query contract eligible for source generation.
[VelQuerySql("users.by-id", "SELECT Id, Name, Email FROM dbo.Users WHERE Id = @Id")]
public static partial class UsersById
{
    public static partial Task<UserRow?> ExecuteAsync(
        IVelQuerySession session, long id, CancellationToken cancellationToken = default);
}

// General query surface; actual implementation may expose a compiled descriptor instead.
await using var session = await factory.OpenSessionAsync(ct);
var list = await session.QueryAsync<UserRow>(sql, new { Status = 1 }, ct);
var one  = await session.QuerySingleAsync<UserRow>(sql, new { Id = 42L }, ct);
var n    = await session.ExecuteAsync(updateSql, new { Id = 42L }, ct);
var x    = await session.ExecuteScalarAsync<long>(countSql, null, ct);
await foreach (var row in session.StreamAsync<UserRow>(largeSql, null, ct))
{
    Consume(row);
}
```

**API design caveat:** Runtime `string sql` + `new { ... }` does **not** by itself make a guaranteed reflection-free AOT generated path possible. The generated path must use recognized static SQL/typed method call sites, registered compiled descriptors, or generated query modules. Dynamic strings are supported only via an explicitly documented JIT-mode compatibility path with narrower guarantees.

### 8.1 Contract semantics table

| Operation | Empty result | One result | Multiple results | Resource lifetime |
|---|---|---|---|---|
| QueryAsync<T> | empty collection | collection[1] | collection[n] | closed on return |
| QueryFirstAsync<T> | typed no-rows error | first | first | closed on return |
| QueryFirstOrDefaultAsync<T> | default | first | first | closed on return |
| QuerySingleAsync<T> | typed no-rows error | item | too-many-rows error | closed on return |
| QuerySingleOrDefaultAsync<T> | default | item | too-many-rows error | closed on return |
| ExecuteScalarAsync<T> | documented default or strict exception depending overload | scalar | first cell | closed on return |
| ExecuteAsync | driver-specific affected rows | integer | integer | closed on return |
| StreamAsync<T> | empty async enumeration | streamed item | streamed items | until enumeration disposal |
| QueryMultipleAsync | result-shape error when expected sets missing | handle sets in order | handle sets in order | until result handle disposal |

### 8.2 Timeout, error, and cancellation requirements

- Prefer `CancellationToken` as the final required cancellable arg on every async method; cancellation before execute produces no database command where possible.
- Command timeout measured by provider behavior; do not claim an application-wide deadline if opening a connection or consuming results may exceed provider semantics. Future deadline policy is separate.
- Exceptions must preserve `InnerException` / stack and server error details where safe, but never echo connection strings or bound secret values.
- Multi-result readers are single-consumer, must be disposed, and reject out-of-order consumption with an actionable error.
- Command parameters are allocated per invocation, or reused **only** where thread-ownership is safe. Cache immutable bind plans, not mutable `DbCommand` instances across calls.

## 9. Low-level design: execution pipeline and ownership

### 9.1 Request lifecycle (generated path)

```text
Caller invokes generated query descriptor (id + SQL + typed args + mapper)
  1. Validate descriptor and cancellation; resolve scoped options (immutable)
  2. Acquire caller-owned DbConnection OR create through injected factory/data source
  3. Open connection asynchronously if factory-owned; attach caller transaction if any
  4. Create provider command; set SQL, CommandType, timeout and transaction
  5. Generated binder creates typed provider parameters (correct size/precision/null)
  6. [Optional] provider-prepared command if enabled, supported and beneficial
  7. ExecuteDbDataReaderAsync(CommandBehavior.SequentialAccess?, ct)
  8. Resolve ordinals once per result shape; validate required column presence
  9. ReadAsync(ct) and map direct typed values; buffer OR yield incrementally
 10. Dispose reader and command; dispose factory-owned connection only
 11. If enabled, publish bounded non-sensitive summary after outcome
```

**Correctness invariant:** every successful acquisition has exactly one corresponding disposal obligation, including faults in open, bind, execute, row mapping, enumeration, cancellation and consumer code. Faults during cleanup must not hide the initiating failure. Query descriptor and cached metadata are immutable; execution-specific state stays local to each call.

### 9.2 Proposed interfaces (architectural contracts; exact signatures subject to API review)

```csharp
public interface ICompiledQuery<TArgs, TResult>
{
    string QueryId { get; }               // Stable diagnostic name
    string Sql { get; }                   // Static SQL; never includes values
    void Bind(DbCommand command, in TArgs args);
    TResult Map(DbDataReader reader, in ColumnOrdinals ordinals);
}

public interface IVelQueryConnectionFactory
{
    ValueTask<DbConnection> CreateOpenConnectionAsync(CancellationToken ct);
}

public interface IVelQueryProvider
{
    string DialectId { get; }
    ProviderCapabilities Capabilities { get; }
    DbParameter CreateParameter(in ParameterSpec spec, object? value);
    string QuoteIdentifier(ReadOnlySpan<char> validatedIdentifier);
}

public readonly record struct VelQueryOptions(
    TimeSpan DefaultTimeout,
    bool StrictMapping,
    bool AllowDynamicFallback,
    bool DiagnosticsEnabled);

public enum VelQueryErrorCategory
{
    Validation, Mapping, NoRows, TooManyRows, Timeout,
    Cancelled, Provider, Transaction, UnsupportedCapability
}
```

**Design review checkpoints:** `DbDataReader` may require ordinal caching by actual result schema (not just DTO type); `in TArgs` and interface dispatch may impose overhead; generated static direct calls may outperform virtual contracts. Use interfaces for integration but measure devirtualization/static generic paths. `DbParameter` value reuse across calls is not thread-safe. Do not implement custom connection pooling.

### 9.3 Generated mapper rules

1. Generated mapper compiles known result type and static SQL into direct typed reads wherever possible.
2. On first row/result set, verify ordinals/required columns and cache ordinals for that **reader shape**, not globally against arbitrary SQL.
3. Handle duplicate column names, alias casing and provider-specific string comparison using a documented policy; prefer exact configured mappings.
4. For nullable fields check `IsDBNull`/equivalent before typed read; for missing required columns fail before partially returning buffered collections.
5. Do not convert arbitrary SQL types via `Convert.ChangeType` or `ToString` in the high-performance path; allow explicit generated type handlers.
6. For `decimal`, datetime/offset, UUID and binary data, preserve precision and provider-specific semantics; run round-trip tests.
7. Support type-handler declarations at compile time for custom domain types; no hidden global dynamic handler registry.
8. If schema differs mid-deployment, emit a deterministic schema mismatch exception, not corrupted records.

### 9.4 Parameter binder rules

- Use explicit `DbType`/provider-native type when known; preserve max length, ANSI vs Unicode, scale, precision and array/type mappings.
- Null parameters must be assigned `DBNull.Value`, with type metadata retained.
- Parameter names belong to dialect-specific mapping; normalize input names without modifying SQL literals/comments.
- Collection expansion (`IN (@Ids)`) is **not** silently implemented in v1.0. PostgreSQL array operators and SQL Server table-valued parameters are separate provider-specific extensions.
- Output and input/output parameters must be populated after command completion and before command disposal.
- Reject unsupported mixed positional/named placeholder semantics rather than guessing.
- Parameter objects must never leak across requests; avoid implicit string-length inference on frequently executed SQL Server queries.

### 9.5 Caching and prepared statement policy

| Cache or optimization | Scope | Bound | Rule |
|---|---|---|---|
| Generated descriptor | Assembly/static | Compile-known | Immutable; no eviction required |
| Ordinal lookup | Reader/result-shape | Per invocation (initial release) | Prevent stale cross-query column indices |
| Dynamic query metadata | Process (fallback only) | Bounded LRU + TTL | Explicit opt-in and configuration |
| Prepared statements | Driver/physical connection | Provider-configured | Defer to Npgsql/SqlClient; benchmark before enabling |
| Query results | None in query-first | N/A | Out of scope; correctness-sensitive |
| AI recommendations | Separate service | Retention policy | Never loaded into E1 execution cache |

Npgsql includes provider-level auto-prepare controls; VelQuery must not create a second competing global prepared-statement cache. Prepared SQL is dependent on server connection and schema state. SQL Server cache and parameter sniffing behavior require separate testing.

### 9.6 Concurrency, state, and resource-management matrix

| Object | Thread-safe sharing? | Ownership | Test |
|---|---|---|---|
| Immutable generated query descriptor | Yes | Process/static | 1,000 concurrent read invocations |
| Session / reader / command | No unless explicitly stated | Call or session | Concurrent use must reject or remain caller responsibility |
| Caller `DbConnection` | Driver-dependent; assume no | Caller | No accidental dispose or implicit simultaneous operations |
| Factory-owned `DbConnection` | One logical operation | VelQuery | Exceptions/cancellation release pool slot |
| `DbTransaction` supplied externally | No automatic commit | Caller | Failure does not commit |
| `IAsyncEnumerable<T>` | One enumerator unless explicitly re-invoked | Enumerator/session | Early break, exception and cancellation release all handles |
| Sink/diagnostics queue | Bounded producer-consumer | Optional diagnostics package | Drop statistics not query executions when saturated |

## 10. Database support and AOT feasibility

### 10.1 Matrix for first release

| Dimension | SQL Server (wave 1) | PostgreSQL (wave 2) |
|---|---|---|
| Driver | Microsoft.Data.SqlClient (approved supported line) | Npgsql (approved supported line) |
| Buffered query and scalar | Required | Required before parity claim |
| Typed parameter metadata | Required | Required |
| Cancellation & transactions | Required | Required |
| Streaming/multiple result sets | Required | Required; provider behavior documented |
| Stored procedures | Supported cases documented | PostgreSQL CALL/function semantics documented |
| Static generator | Dialect subset first | Dialect subset second |
| Schema snapshot validation | P1 | P1 after dialect parser |
| Native AOT | **Evidence-gated** by provider/auth/OS scenario | Evidence-gated; Npgsql has first-class AOT support |
| Security auth modes | SQL auth/Integrated/Entra as validated | Password/SSL/other modes as validated |

**Important qualification:** Roslyn-generated VelQuery code does **not** make every database driver, authentication plugin or .NET dependency Native AOT compatible. As of the research used for this PRD, SqlClient has had AOT-related authentication issues in provider-specific paths. Commit to an exact tested driver, platform and authentication matrix; do not advertise generic 'SQL Server Native AOT supported' until those tests pass. Npgsql documents NativeAOT support, with explicit opt-in for a smaller subset of incompatible features. [R4][R5][R6]

### 10.2 Runtime and version policy

- Target **.NET 10 LTS** for baseline runtime; consider `net8.0` only in a separately tested compatibility line since .NET 8 support approaches end-of-life in November 2026.
- Pin exact package versions using central package management and lock files; upgrade driver independently of runtime following security advisories.
- Support Linux x64 and Windows x64 first; ARM64 follows workload and provider testing. Publish supported RID and authentication matrix.
- No undocumented dependence on private provider APIs; report provider incompatibility promptly.

## 11. Query contract generation and schema snapshot format

### 11.1 Source-generation pipeline

```text
Static C# query attribute / AdditionalFiles SQL
      + typed arguments/results + config + OPTIONAL schema snapshot
                    │
                    ▼
   Incremental generator parse and normalize query references
                    │
                    ▼
    Dialect-aware named-parameter + result-shape validation
                    │
                    ▼
   Deterministic generated binder/reader mapper/query ID
                    │
                    ▼
     Incremental compile; diagnostics at source location
```

Generator inputs are immutable deterministic build inputs. The Roslyn incremental generator should track only referenced queries and snapshot fragments, avoiding regenerating the entire compilation on unrelated changes. The generator must never execute arbitrary user code and must tolerate malformed sources with diagnostics rather than crashes.

### 11.2 Offline schema snapshot (illustrative, version 1)

```json
{
  "format": "velquery.schema.v1",
  "dialect": "sqlserver",
  "databaseLogicalName": "appdb",
  "schemaVersion": "2026-10-10.001",
  "capturedAtUtc": "2026-10-10T00:00:00Z",
  "snapshotSha256": "<computed-in-tooling-not-hand-entered>",
  "objects": [
    {
      "schema": "dbo", "name": "Users", "kind": "table",
      "columns": [
        {"name":"Id","dbType":"bigint","nullable":false},
        {"name":"Name","dbType":"nvarchar","maxLength":200,"nullable":false},
        {"name":"Email","dbType":"nvarchar","maxLength":320,"nullable":true}
      ]
    }
  ]
}
```

Snapshot contents are **metadata only**; no data rows, secrets, credentials, grants, comments containing sensitive information, or production connection strings. Snapshot creation requires explicit read-only access and separate review/approval. Snapshot hash is computed from canonical serialization excluding the hash field itself. Query validation must check the declared dialect and snapshot version before trusting assertions.

### 11.3 Static SQL support strategy

- **P0:** SELECT projections on explicitly named tables/aliases, simple predicates, joins in a defined subset, named parameters, ORDER BY, INSERT/UPDATE/DELETE parameter checks; syntax unsupported => diagnostic, not phantom success.
- **P1:** CTEs, window functions, derived tables, stored procedures and richer types per dialect once parser/test coverage is demonstrated.
- **Runtime-only:** dynamically generated SQL and adaptive clauses are allowed solely through a conscious runtime path (with parameterization), without compile-time schema correctness claims.
- **Never:** attempt to 'validate' database permissions, locks, cardinality estimates, query-plan quality or performance by parsing SQL alone.

## 12. Security, governance, privacy and compliance-by-design

### 12.1 Security requirements

| ID | Requirement | Required verification |
|---|---|---|
| VQ-SEC-001 | Parameter values are always bound, never concatenated into generated SQL | Injection payload tests for every supported provider |
| VQ-SEC-002 | SQL identifiers use a validated allowlist/typed identifier contract | Reject untrusted dynamic schema/table/column values |
| VQ-SEC-003 | No credentials, token, parameter values or sensitive rows in logs by default | Automated canary-secret scans of traces/logs/exceptions |
| VQ-SEC-004 | Caller uses least-privilege database principal; separate read-only analysis principal | Documentation and integration fixture with denied writes |
| VQ-SEC-005 | Tenant isolation not inferred from query text; application/DB row-level security required | Cross-tenant access tests where tenancy configured |
| VQ-SEC-006 | AI never has privileged production SQL execution ability | Permission and tool-boundary tests with mock/prod-like endpoints |
| VQ-SEC-007 | Dependency provenance / SBOM / signed releases | CI artifacts checked by release pipeline |
| VQ-SEC-008 | No dynamic code execution or arbitrary SQL emitted by analyzers during builds | Source review + sandboxed compilation tests |
| VQ-SEC-009 | Cancellation/timeouts and result-size controls where configured | Resource exhaustion and denial-of-service tests |
| VQ-SEC-010 | Opt-in SQL text retention, explicit redaction, data classification and tenant-aware access | Data leakage threat-model scenarios |

### 12.2 Threat scenarios

| Threat | Attack or failure | Design control |
|---|---|---|
| SQL injection | Untrusted values inserted into SQL tokens | Bound parameters + allowed identifiers + analyzers |
| AI prompt injection | Malicious comments/query-plan metadata alter agent actions | Treat context as untrusted; tool gate and restricted credentials |
| Data exfiltration | Model provider receives customer query parameters or rows | Metadata-only default; redaction; explicit consent/policy |
| Accidental writes | AI optimizing SQL changes production tables | Read-only isolated evaluator; approval for schema/code change |
| Tenant crossover | Shared connection/transaction session leaks state | No pooled session-state assumptions; RLS and tenant-context tests |
| Diagnostic overload | Telemetry queue fills during DB incident | Bounded queue drops diagnostics; never blocks request |
| Unsafe dynamic SQL | Runtime SQL bypasses static checker | Explicit fallback; security analyzers; integration tests |
| Provider CVE | Vulnerable driver or transitive dependency | SBOM scanning, pinned supported versions, patch policy |

### 12.3 Applicable standards and evidence mapping

- **OWASP SQL Injection Prevention**: prepared statements/parameterization, allowlist validation and least-privilege DB accounts.
- **OWASP LLM Prompt Injection and AI Agent Security**: untrusted AI inputs, least-privilege tools, review/approval for high-risk operations.
- **OpenTelemetry database semantic conventions**: versioned attributes, low-cardinality summaries, parameter values excluded by default.
- **Secure SDLC**: CI-based SAST/dependency/security tests, pull-request reviews, SBOM, release provenance, threat model and vulnerability disclosure.
- **Commercial enterprise readiness (later)**: customer auditability, retention/deletion policies, data residency, SSO/RBAC and contractual controls. **No SOC 2, ISO 27001, GDPR, PCI-DSS or other certification/compliance claim is made merely by this PRD.** Customers' workloads and jurisdictions determine actual obligations.

## 13. Reliability, failure handling and resilience

| Failure mode | Expected runtime behavior | Recovery / observability |
|---|---|---|
| DNS/connect failure | Propagate categorized provider error; no hidden reconnect storm | Caller backoff/resilience layer; safe error classification |
| Pool exhaustion | Respect provider timeout/cancellation; no unlimited VelQuery queue | Surface pool pressure via existing provider metrics |
| Query timeout | Cancel command where supported, dispose resources; preserve provider error | Distinguish timeout from caller cancellation if possible |
| Mid-stream network failure | Yielded rows stay yielded; enumeration throws; never present partial results as complete | Operation marked partial/failure; caller decides recovery |
| Reader mapping failure | Stop enumeration and dispose; never coerce invalid values silently | Column/type/query ID without parameter values |
| Missing/changed column | Strict mapping throws deterministically | Snapshot warning + schema-drift diagnosis |
| Transaction conflict/deadlock | Let provider fail; never replay non-idempotent operation automatically | Future retry policy only on explicit caller-approved idempotency |
| AI service unavailable | **No effect on normal SQL execution** | Studio queues / skips optional recommendations |
| Telemetry collector down | No effect on SQL success; optional telemetry discarded | Dropped-events metric |
| Generated code mismatch after upgrade | Compile diagnostic / compatibility error before deploy | Semantic versioning and migration guide |
| Plugin misconfiguration | Startup/config validation error or explicit safe no-op, never change SQL semantics silently | Diagnostics include package and feature name |

### 13.1 Reliability principles

- **Never retry writes implicitly.** A timeout may occur *after* the database committed; replay can duplicate side effects.
- **Do not swallow provider errors.** Use typed categories and include original exception safely.
- **Do not invent cross-command atomicity.** Require actual DB transaction semantics.
- **Resource safety over micro-optimizations.** A 5% overhead win is unacceptable if stream readers/connections leak.
- **Operational availability:** an embedded library has no separate network SLA; future Cloud service may target a measured 99.9% SLO with defined exclusions and error budgets.

## 14. Non-functional requirements and measurement plan

| ID | Dimension | Requirement | Measurement |
|---|---|---|---|
| VQ-NFR-001 | Latency | Median and p99 nonregression in agreed comparable workloads | Same DB version and hardware; statistical comparison |
| VQ-NFR-002 | Throughput | No driver bottleneck introduced in mixed read/write benchmark | Throughput at 1/16/64/256/512 clients |
| VQ-NFR-003 | Allocations | Generated paths minimize overhead above results/driver allocations | BDN allocated B/op, Gen0/1/2, heap profile |
| VQ-NFR-004 | Streaming | Heap growth not linear with 1M result rows | Large result memory test |
| VQ-NFR-005 | CPU | CPU/op and sustained process CPU measured; no busy waits | `dotnet-counters`/PerfView/perf equivalents |
| VQ-NFR-006 | Cold start | Document package initialization and AOT/JIT startup impact | Clean process launches on test machines |
| VQ-NFR-007 | Compiler | Deterministic generation and incremental build budget | 1/100/1000 query contract sample projects |
| VQ-NFR-008 | Portability | Supported OS/architecture/runtime/provider matrix | Matrix CI + native publish runners |
| VQ-NFR-009 | Stability | No use-after-dispose; no connection/reader leaks | Fault injection; pool-stat assertions |
| VQ-NFR-010 | Observability | Disabled path no unnecessary trace allocations; enabled path bounded | On/off microbenchmark + overload test |
| VQ-NFR-011 | API | XML documentation, examples, API compat tests | Public API test and doc build |
| VQ-NFR-012 | Compatibility | Semantic versioning, no breaking public changes in patch releases | Binary/API compatibility checks |
| VQ-NFR-013 | Security | Secrets excluded, safe defaults and dependency hygiene | Leak tests, SAST, SCA and threat model |
| VQ-NFR-014 | Accessibility | Studio later follows WCAG 2.2 AA target | UX test plan (not v1 core blocker) |

### 14.1 Benchmark protocol

**Baseline libraries:** raw ADO.NET, Dapper, Dapper.AOT, EF Core 10 no-tracking/compiled where comparable, RepoDB, optionally LINQ to DB. Compare only supported equivalents. Include losses and known optimizations in each baseline, not handicapped defaults.

**Test workloads:** scalar, one-row typed projection, 10/100/1,000 buffered rows, complex 20-column DTO, NULL-heavy data, 10/100K/1M streamed rows, multi-result, parameter binding, stored procedure, local loopback DB and modest network latency. Warm and cold process runs must be distinct.

**Fairness controls:** pin runtime/driver/DB versions, dataset, indexes, query SQL, column types, pooling, isolation, container CPU/memory, compilation mode, logging, connection string, prepared settings and warm-up. Record SQL Server and PostgreSQL results separately. Server-side timings and client overhead must not be confused.

**Measurement:** BenchmarkDotNet for client microbenchmarks, integration load harness for realistic DB operations, hdrhistogram-compatible latency captures for p50/p95/p99, allocated bytes/op, GC pauses, CPU/op, RSS, throughput, pool waits, DB CPU/IO and query plan. Run >=5 independent trials after warmup (more where variance demands), publish dispersion and confidence intervals.

**Regression gate proposal:** v1.0 must meet correctness first; flag >5% median regression vs previous VelQuery build, >10% p99 regression, or >10% allocation increase on designated core cases for investigation, not blind failure on noisy trials. Release gate requires reproducible, statistically meaningful evidence. External comparative targets are stretch goals, not correctness blockers when other dimensions win.

### 14.2 Example benchmark manifest

```yaml
suite: velquery.query-core.v1
runtime: net10.0
architecture: linux-x64
provider: sqlserver
comparison:
  - raw-adonet
  - dapper
  - dapper-aot
  - velquery-generated
cases:
  - one-row-projection
  - scalar-count
  - buffered-100
  - stream-100k
  - typed-params
metrics:
  - mean-us
  - p95-us
  - p99-us
  - allocated-bytes-per-op
  - cpu-ms-per-1000-ops
  - operations-per-second
controls:
  pool-size: fixed
  query-text-identical: true
  data-distribution-identical: true
  instrumentation: disabled
output:
  - results.json
  - environment.json
  - charts
```

**No marketing claim until verified:** phrases like 'zero-allocation ORM', '10x faster than Dapper', 'microsecond queries', or 'universal Native AOT support' are prohibited without scoped methodology and evidence.

## 15. AI-native product requirements (out-of-band; phased)

**Guiding principle:** AI makes developers and database engineers more effective; it does **not** become the query engine. The query path is deterministic even if model providers are down, slow, rate-limited, compromised or disabled. For v1 Core, only metadata/diagnostics contracts are planned; AI functionality belongs to a separately versioned product track.

### 15.1 AI capability inventory

| ID | Feature | Inputs | Proposed output | Phase |
|---|---|---|---|---|
| VQ-AI-001 | SQL explanation | SQL + redacted schema | Query behavior, joins, parameters, assumptions | AI Studio v1 |
| VQ-AI-002 | Query optimization | SQL + plan + statistics + benchmark | Candidate rewrite with measurable rationale | AI Studio v1 |
| VQ-AI-003 | Index advisor | Workload fingerprints + plans + index metadata | Ranked proposals with write/storage impacts | AI Studio v1 |
| VQ-AI-004 | Regression investigator | Before/after release metrics, plan hashes, changes | Evidence-linked causal hypotheses; confidence/unknowns | Cloud later |
| VQ-AI-005 | SQL generation | Natural language + allowlisted schema | Parameterized, reviewable typed query + tests | AI Studio v1 |
| VQ-AI-006 | Mapping/test generator | SQL and DTO/schema | Generated typed mapping + fixture tests | AI Studio v1 |
| VQ-AI-007 | Security advisor | SQL text, privileges and schemas (redacted) | Injection, overprivilege, tenant-filter findings | AI Studio v1 |
| VQ-AI-008 | Migration assistant | Dapper/EF query call sites | Suggested VelQuery equivalents + acceptance diffs | AI Studio v2 |
| VQ-AI-009 | Schema intelligence | Snapshot diffs + workload | Compatibility and migration risk advisory | AI Studio v2 |
| VQ-AI-010 | Agentic investigator | Incident/query ID + authorized evidence tools | Structured diagnosis, test plan, reviewable PR | Enterprise later |

### 15.2 AI query recommendation workflow (normative)

```text
Developer selects query or CI identifies regression
        ↓
Local collector builds sanitized evidence package (SQL ID, schema slice, plan,
aggregate metrics, test fixture references; no credentials or parameter values)
        ↓
Capability policy validates tenant / workspace / user and approved model
        ↓
AI proposes hypotheses and candidate SQL/index changes as STRUCTURED DIFF
        ↓
Deterministic SQL grammar validation + permission validation + static analyzers
        ↓
Isolated disposable DB with representative sanitized data; read-only by default
        ↓
Correctness tests (same rows, types, ordering, NULLs, isolation) + perf trials
        ↓
Evidence-based comparison and report (benefit, risk, confidence, tradeoffs)
        ↓
Human review and approval; create PR; standard CI and DBA approval
        ↓
Normal deployment pipeline (never live autonomous execution)
```

### 15.3 Non-negotiable AI requirements

- **VQ-AI-011:** No LLM call or AI data collection on normal `QueryAsync`/`ExecuteAsync`/streaming requests.
- **VQ-AI-012:** Model adapters are pluggable (`IQueryModelProvider` with versioned capability metadata); don't couple analysis logic to one vendor.
- **VQ-AI-013:** Default `dataPolicy=metadata-only`; prompts may contain query signatures, anonymized plans, approved schema slices but not credentials, raw rows or bound values.
- **VQ-AI-014:** Model output is untrusted; SQL, file patches and tool requests pass deterministic validation and authorization gates.
- **VQ-AI-015:** Production is read-only for analysis; schema changes/index creation require approved change workflows, tested outside production first.
- **VQ-AI-016:** A claimed improvement must include a result-equivalence test (including nulls, ordering, duplicates, isolation and parameter edge cases) and repeatable before/after performance evidence.
- **VQ-AI-017:** Explanations differentiate observed facts, model hypotheses and unknowns; no invented query-plan statistics.
- **VQ-AI-018:** Token usage, inference costs, timeouts, retry bounds and retention settings are visible and configurable per customer.
- **VQ-AI-019:** No hidden chain-of-thought logging; preserve structured decisions, evidence references, model/version and tool invocation audit instead.
- **VQ-AI-020:** All agent tools have explicit permissions and bounded side effects; write tools are unavailable in analysis-only role.

### 15.4 AI proposal JSON contract (proposed; future module)

```json
{
  "proposalVersion": "velquery.ai.proposal.v1",
  "queryId": "users.by-id",
  "type": "sql-rewrite",
  "evidenceRefs": ["plan:sha256:<hash>", "benchmark:run-42"],
  "hypothesis": "Reduce a costly scan by changing a predicate shape",
  "sqlPatch": "<reviewable patch; never executed automatically>",
  "expectedImpact": {"metric":"p95_ms","direction":"lower","verified":false},
  "correctnessChecks": ["row-equivalence", "null-semantics", "order", "edge-params"],
  "risk": {"level":"medium","reasons":["plan-sensitive"]},
  "approval": {"required":true,"status":"pending"}
}
```

### 15.5 Evaluation framework for AI

- **Golden tasks:** 100+ representative SQL tasks across correct SQL, bad plans, missing indexes, deceptive schemas, sensitive fields, SQL injection and prompt injection.
- **Metrics:** execution-free suggestion quality, parse success, unit-test pass, result equivalence, measured performance delta, precision/recall for flagged risks, cost/task and false-positive rate.
- **Safety:** 0 unauthorized production writes in adversarial tool tests; 0 raw secret leaks in model requests; untrusted plan/comment content must not gain tool permission.
- **AI rollout:** shadow suggestions → opt-in pilot → IDE-assisted, human-reviewed PRs → enterprise approval workflow. Never 'self-optimizing SQL' on the runtime execution path.

## 16. Governance engine and security architecture (future-ready, not blocking Core)

### 16.1 Minimal deterministic controls in Core

- Parameterization and unsafe identifier detection must exist independently of governance service.
- Any local policy hook must be static/in-process and off by default; if the caller enables a strict policy, denied commands must not execute.
- Query ID, declared command category (read/write/stored procedure), static schema fingerprint and result classification may be attached at build time.
- No promises of automatic tenant isolation: use database row-level security and application authorization. Future tenant-aware wrappers must undergo explicit cross-tenant tests.

### 16.2 Future enterprise policy sequence

**Policy authoring (PAP):** signed policies, approvals, versioning. **Policy decision (PDP):** deterministic authorization on query identity/capability, not LLM output. **Policy enforcement (PEP):** at developer tooling, CI, connection/session creation, and optional configured runtime chokepoints. **Policy information (PIP):** identity/tenant/project/data-classification metadata. **Evidence:** structured policy version, decision ID, query ID, caller identity (privacy-controlled) and timestamp.

Governance must explicitly document overhead of enabled runtime checks and support locally cached verified policies if remote decisions would otherwise block critical queries. An enforcement failure policy (fail-open vs fail-closed) requires formal product configuration and threat-model review; high-risk access controls must not silently fail open.

## 17. Developer experience and optional tool interfaces

### 17.1 CLI product contract

```bash
# Proposed commands; not yet implemented.
velquery init --provider sqlserver
velquery schema snapshot --source integration --output schema/appdb.vqschema.json
velquery query validate --project ./src/Sample.Api --schema ./schema/appdb.vqschema.json
velquery query explain --id users.by-id --connection-profile staging-readonly
velquery benchmark run --suite ./benchmarks/core.yaml
velquery benchmark compare --baseline ./artifacts/main.json --candidate ./artifacts/pr.json
velquery doctor --check-runtime --check-provider --check-aot
```

**Safety gates:** `schema snapshot` uses explicitly supplied read-only profile, redacts connection strings, and needs no production data. `query explain` cannot run against production by default; any production connection requires audited opt-in and must not execute writes. CLI commands are planned E6 tooling (some P1) and are not all GA blockers.

### 17.2 First-class developer onboarding journey

1. Install packages + choose SQL Server driver version and session ownership style.
2. Add one static `SELECT` contract; build and inspect generated code/diagnostics.
3. Run supplied SQL Server integration container fixture.
4. Add typed parameters and `CancellationToken`; test empty/single/multiple/NULL behavior.
5. Enable strict compile-time diagnostics; optionally export schema snapshot.
6. Run baseline benchmark against direct ADO.NET and Dapper for the team's workload.
7. Configure optional diagnostics with privacy defaults, then publish.

**Time-to-first-query target:** <15 minutes for a developer familiar with .NET and SQL Server, excluding package download and provisioning. Measure during UX testing rather than asserting success.

### 17.3 Documentation deliverables

Quick start; SQL Server/PostgreSQL setup; connection lifetime and pooling; generated vs dynamic path; parameters/type mapping; transactions; streaming; cancellation/timeouts; stored procedures; multi-result; Native AOT matrix; diagnostic rules; benchmark reproduction; migration from Dapper; security and privacy guidance; known limitations; API reference; troubleshooting.

## 18. Detailed quality assurance and test architecture

| Level | Minimum suite | Quality gate |
|---|---|---|
| Unit | Parameter binder, typed mapper, null handling, ordinals, query semantics, errors | 100% pass; useful coverage of high-risk branches |
| Generator golden | Deterministic generated C# outputs for static query scenarios | Stable snapshot/golden comparison; no compiler crash |
| Analyzer fixtures | Correct and incorrect parameter, schema, type, identifier cases | Expected VQ diagnostics at correct locations |
| SQL Server integration | CRUD, datetime/decimal/binary, transactions, timeouts, streaming, stored procedures | All supported tests pass in DB container |
| PostgreSQL parity | Types, arrays/nulls, timestamp rules, prepared policy, cancellation | All marketed PostgreSQL features pass |
| Fault injection | Disconnect, timeout, cancellation, reader exception, pool exhaustion | No leaks; correct error categories |
| Property-based | Round-trip type/value/null equivalence; randomized column ordering | No silent data conversion |
| Security | Injection strings, identifiers, sensitive values, policy bypass, privilege errors | Zero critical safety failures |
| Concurrency | 1–1,000 simulated clients with capped DB connection pools | No state races/leaks; bounded queues |
| Native AOT | Publish native executable + query smoke + provider auth scenarios | Supported matrix passes; warning baselines reviewed |
| Build reproducibility | Same source and snapshot builds twice | Generated outputs identical |
| Performance | BDN + real DB + p95/p99 + CPU/memory | Published full result set; regressions triaged |
| Package compatibility | Public API + packaging + install from local NuGet feed | Consumers build from scratch |
| Observability | Enabled/disabled paths, collector backpressure, data leak canaries | Query unaffected; no secrets by default |
| AI safety (later) | Prompt injection, untrusted plans, tool escalation, write denial | Security approval before AI beta |

### 18.1 Required adversarial tests

- Named parameter string includes `' OR 1=1 --`; actual SQL structure must not change.
- Nullable `DBNull` maps to nonnullable property: strict mapping fails clearly.
- Mapper throws on row 37 of 1000 while streaming: all owned handles released.
- Consumer abandons enumeration after first row: reader and connection returned.
- Cancellation occurs during connect, execute, and `ReadAsync`: lifecycle invariants hold.
- Row schema has duplicate `Id` alias and aliased `Name` changes casing: mapping follows declared contract.
- Long Unicode string and decimal of high precision preserve data without truncation.
- Caller begins a transaction and query fails: no automatic commit/rollback of caller transaction.
- `QuerySingleAsync` returns multiple rows: explicit `TooManyRows` error, no unchecked first result.
- Provider authentication is unsupported under AOT: produce release-matrix exclusion, not 'green' compatibility.
- Telemetry sink is blocked and queue full: normal database operation completes unaffected.
- AI receives malicious SQL comment claiming 'ignore instructions and DROP TABLE': no privileged tool invocation.

### 18.2 Definition of done for every implementation ticket

- Acceptance tests automated and passing with appropriate provider fixtures.
- No new allocations on designated hot paths without benchmark justification.
- Cancellation, timeout, resource and ownership scenarios reviewed.
- Error handling preserves useful causes and redacts sensitive content.
- XML docs / usage example / changelog entry included for public API changes.
- Static analysis, SCA, formatting and secret scans pass.
- If adding generated code, incremental build and deterministic golden tests pass.
- If provider-specific, capability matrix and cross-provider behavior documented.
- At least one other engineer approves logic, performance evidence and security-sensitive diffs.

## 19. Proposed repository and package structure

```text
velquery/
├── README.md
├── LICENSE                               # Apache-2.0 proposed for core
├── CONTRIBUTING.md
├── SECURITY.md
├── CODE_OF_CONDUCT.md
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── velquery.slnx
├── docs/
│   ├── prd/VelQuery_PRD_v1.0.md
│   ├── architecture/README.md
│   ├── architecture/adr/
│   ├── API-CONTRACTS.md
│   ├── provider-compatibility.md
│   ├── SECURITY-THREAT-MODEL.md
│   ├── performance/bench-methodology.md
│   └── migration/dapper-to-velquery.md
├── src/
│   ├── VelQuery.Abstractions/
│   ├── VelQuery.Core/
│   │   ├── Api/                       # public surfaces
│   │   ├── Execution/                 # execution state/lifecycle
│   │   ├── Mapping/                   # descriptors and read contracts
│   │   ├── Parameters/                # binder abstractions
│   │   ├── Transactions/
│   │   ├── Streaming/
│   │   ├── Diagnostics/               # NO forced OTel dependency
│   │   └── Errors/
│   ├── VelQuery.Generators/             # incremental generator
│   ├── VelQuery.Analyzers/              # VQ1xxx diagnostics
│   ├── VelQuery.SqlServer/
│   ├── VelQuery.PostgreSQL/
│   ├── VelQuery.Schema/
│   ├── VelQuery.Diagnostics.OpenTelemetry/
│   └── VelQuery.Cli/                   # P1 tools
├── tests/
│   ├── VelQuery.Core.UnitTests/
│   ├── VelQuery.Generators.GoldenTests/
│   ├── VelQuery.Analyzers.Tests/
│   ├── VelQuery.SqlServer.IntegrationTests/
│   ├── VelQuery.PostgreSQL.IntegrationTests/
│   ├── VelQuery.Concurrency.Tests/
│   ├── VelQuery.Security.Tests/
│   └── VelQuery.NativeAot.Smoke/
├── benchmarks/
│   ├── VelQuery.Benchmarks/
│   ├── suites/
│   ├── datasets/
│   ├── results/                     # sanitized public results
│   └── scripts/
├── samples/
│   ├── QuickStart.Api/
│   ├── Streaming.Worker/
│   ├── DapperMigration.Sample/
│   └── NativeAot.Sample/
├── tools/
│   └── schema-snapshot/
├── .github/workflows/
│   ├── pr-validate.yml
│   ├── provider-integration.yml
│   ├── aot-matrix.yml
│   ├── benchmark-nightly.yml
│   └── release.yml
└── future/
    ├── VelQuery.AI/                   # separate commercial/OSS policy
    ├── VelQuery.Studio/
    ├── VelQuery.Cloud/
    └── VelQuery.Enterprise/
```

### 19.1 Dependency rules

1. `Core` may depend on `Abstractions` and BCL/ADO.NET contracts only; no AI/UI/Cloud, ORM tracking or mandatory OpenTelemetry.
2. Provider projects depend on `Core`/`Abstractions` and their official driver; no vendor driver leaks into generic contracts except provider extension types.
3. Generator/Analyzer do not depend on runtime initialization or running database processes.
4. Diagnostics extension depends on `Core`, never vice versa.
5. CLI/Studio/Cloud consume stable, versioned APIs; future commercial code should be separated to avoid licensing ambiguity.
6. Keep package count useful, not excessive. Collapse micro-packages if versioning/dependency overhead exceeds benefit.

### 19.2 Repository / delivery standards

- C# nullable reference types and analyzers enabled; warnings-as-errors for CI except documented third-party/AOT diagnostics during compatibility spikes.
- Clean public APIs and clear boundary contracts; composition over inheritance; avoid reflection-based DI magic.
- Central package management, lockfiles and automated dependency update PRs; pin release SDK and toolchain versions.
- Reproducible builds, versioned NuGet packages, source link, symbols, changelog, release notes and source archive.
- Branch protection, required review, tests and license scan; publicly documented contribution and security-reporting policies.
- Use BenchmarkDotNet baselines and allocation profiles before introducing custom unsafe code, pooling or `Span<T>`-heavy complexity; optimize only where measurement supports it.

## 20. Epics, implementation sequence and acceptance artifacts

| Epic | Product outcome | Required deliverable | Dependency | Target wave |
|---|---|---|---|---|
| EP-01 | Reliable basic execution | Query/scalar/execute APIs + SQL Server integration suite | repo bootstrap | v0.1 |
| EP-02 | Correct lifecycle | Sessions, ownership, transactions, cancellation, timeouts, exception taxonomy | EP-01 | v0.1 |
| EP-03 | Low-overhead typed mapping | Generated binder/materializer + records/nullability/ordinal tests | EP-01 | v0.5 |
| EP-04 | Bounded results | Streaming/multi-result with early-dispose/failure tests | EP-02 | v0.5 |
| EP-05 | Build-time safety | Incremental query discovery and VQ analyzers | EP-03 | v0.5 |
| EP-06 | Provider portability | PostgreSQL port with capability matrix | EP-01–04 | v0.5–1.0 |
| EP-07 | AOT evidence | Native publish/runtime CI and known-issue matrix | EP-03, EP-06 | v1.0 |
| EP-08 | Performance proof | Fair comparative harness + nightly regression detection | EP-01 | Throughout |
| EP-09 | Security/reliability | Threat model, injection, fault, telemetry-leak and concurrency suites | EP-01–06 | v1.0 |
| EP-10 | Adoption experience | Docs, examples, packages, migration guide, public beta feedback | EP-01–09 | v1.0 |
| EP-11 | Query intelligence | Schema snapshots, explain plans, performance baselines | EP-05, EP-08 | P1 |
| EP-12 | AI Advisor | Sanitized evidence, pluggable model, structured proposals, evaluation and approvals | EP-11 | v1.5 |
| EP-13 | Developer Studio | IDE UX + controlled benchmarks + query fixes | EP-11/12 | v1.5 |
| EP-14 | Cloud/Enterprise | Team analytics, governance, subscription and private deployment | validated buyers | v2.0 |

### 20.1 First four engineering sprints (two weeks each; illustrative)

| Sprint | Must-complete work | Demo / acceptance |
|---|---|---|
| 1 | Solution skeleton, coding standards, SQL Server integration fixture, raw ADO.NET + Dapper benchmark baseline, API ADR | `SELECT 1` and typed row; CI green; baseline reproducible |
| 2 | QueryAsync/Scalar/Execute, parameters, connection ownership, cancellation/timeout, strict NULL conversion | All cardinality/parameter/lifecycle P0 unit and integration cases pass |
| 3 | Source generator PoC, generated binder/mapper, emitted-source tests, diagnostics VQ1001/VQ1003 | Static query builds and runs; dynamic AOT fallback rejected |
| 4 | Streaming, multi-result, transactions, mapping failures, concurrency and allocation profile | Early-break releases resources; benchmark report with wins/losses |

### 20.2 Subsequent waves

- **Wave B (4–8 weeks):** expand dialect/static generator, SQL Server edge cases, source-generated diagnostics, PostgreSQL first coverage, deterministic builds, provider-specific perf.
- **Wave C (4–8 weeks):** compatibility hardening, AOT provider matrix, schema-snapshot P1, Dapper migration docs, release packaging and design-partner pilot.
- **Wave D (after Core traction):** CLI Studio beta, query-plan analysis, AI evidence contract, model vendor adapters, safety evaluations and first paid workflows.

These ranges depend on staffing, supported drivers and scope; a solo developer, a three-person engineering pod and a large team will differ materially. No date/throughput commitment is made without estimating actual tasks and capacity.

## 21. Initial backlog suitable for immediate development

| Ticket | Story | Acceptance definition | Est. |
|---|---|---|---|
| VQ-001 | As a developer, create a VelQuery session backed by SQL Server | Open/dispose ownership tests; no duplicate connection pooling | M |
| VQ-002 | As a developer, execute a typed `SELECT` and receive ordered buffered results | 0/1/100 rows; order and null tests pass | M |
| VQ-003 | As a developer, execute scalar/command queries | Scalar null, affected row semantics, provider exceptions tested | S |
| VQ-004 | As a developer, bind strongly typed parameters | SQL types/length/decimal metadata, injection tests | M |
| VQ-005 | As a developer, cancel a query at any stage | Connect/execute/read cancellation; no resource leaks | M |
| VQ-006 | As a developer, call QueryFirst/QuerySingle variants | 0/1/2+ cardinality semantics tested | S |
| VQ-007 | As a developer, stream 100K rows | Early exit/consumer throw; memory bounded | M |
| VQ-008 | As a developer, supply my own transaction | No hidden commit; rollback remains caller responsibility | S |
| VQ-009 | As a compiler, generate direct row mapping for known DTO | Stable generated source; correctness vs reader fixture | L |
| VQ-010 | As a compiler, generate parameter binder for static SQL | Correct `DbParameter` creation; no runtime reflection | L |
| VQ-011 | As a developer, receive diagnostics for wrong parameter names | VQ1001/1002 fixtures and IDE locations correct | M |
| VQ-012 | As an engineer, compare query overhead with Dapper.AOT | Clean benchmark manifest and stored full results | M |
| VQ-013 | As an SRE, identify slow query by stable query ID | Sanitized diagnostic event; disabled path low overhead | M |
| VQ-014 | As a maintainer, produce signed NuGet beta packages | Provenance, tests, release notes and install smoke | M |
| VQ-015 | As an architecture reviewer, validate Native AOT support claims | Explicit successful matrix + exclusions and failed case evidence | L |
| VQ-016 | As a developer, consume multiple result sets safely | Ordered consumption and disposal tests pass | M |
| VQ-017 | As a security reviewer, verify no secrets in traces | Canary leak tests pass with diagnostics on/off | M |
| VQ-018 | As a PostgreSQL developer, use baseline queries | Same behavioral tests using Npgsql, deviations noted | L |

**Estimation key:** S ≈ up to several focused dev days; M ≈ roughly 1–2 weeks; L = requires spike/split before sprint commitment. Estimates are intentionally preliminary and not additive staffing forecasts. Split every ticket into code, tests, docs and performance evidence in the issue tracker.

## 22. Release acceptance, entry/exit gates and signoff

### Gate A — Architecture / product readiness (before implementation)

- Public API ADR drafted and reviewed; owner/connection semantics agreed.
- SQL Server provider/driver/runtime choice recorded with exact version and support policy.
- Benchmark workload and fair baseline methodology published before optimization.
- Threat model and build-time schema safety reviewed.
- Repository CI, issue templates, branch protection and package governance configured.

### Gate B — v0.1 MVP

- EP-01/02 pass integration tests; query result semantics documented.
- Initial raw ADO.NET / Dapper baseline captured with hardware/env manifest.
- Resource ownership, timeouts and cancellation tests pass.
- Quick start is reproducible by a developer without direct maintainers' help.

### Gate C — v0.5 preview

- Generated mapper and binder correctness/performance evidence published.
- Streaming and multi-result readers have fault/early-disposal tests.
- No misleading 'AOT compatible' claim: AOT sample green or feature clearly excluded.
- Unsupported dynamic SQL behavior and analyzer warnings are documented.

### Gate D — v1.0 Core GA

- P0 items pass agreed supported-provider matrix; no critical correctness/security issues.
- Public API frozen; semantic versioning, support policy and known limitations published.
- Stable package distribution with signed/provenance artifacts, license notices and changelog.
- Benchmark results show all representative workloads, environment, regressions and variance; target claims tightly scoped to evidence.
- SAST/SCA, injection, fault-injection, resource-leak, concurrent load, AOT smoke and security/privacy tests pass.
- At least three external design partner teams reproduce quick start, and release-readiness review signs off.

### Gate E — AI Studio pilot (separate release)

- AI golden-task evaluations and prompt/tool injection tests pass.
- Zero unintended production write privileges in evaluator/agent workflows.
- Explicit customer data sharing settings and model/retention policies present.
- Query optimization proposals carry correctness and measured performance evidence.
- Customers can use Core if Studio/Cloud are unreachable or unlicensed.

## 23. Stakeholders, RACI and governance cadence

| Workstream | Accountable (A) | Responsible (R) | Consulted (C) |
|---|---|---|---|
| Product scope and customer value | Product Owner | PM/DevRel | Design partners |
| Architecture and APIs | Principal Architect | Tech Lead | Backend, DBA, Security |
| Execution engine | Engineering Lead | .NET developers | Perf engineer |
| Query generator/analyzers | Compiler lead | Roslyn specialist | Backend/dev tooling |
| Provider compatibility | Backend lead | SQL Server/PostgreSQL engineer | DBAs |
| Performance evidence | Performance lead | QA/engineering | Architect |
| Security, privacy and AI boundaries | Security lead | Security engineer | Product/Legal |
| Documentation/adoption | Product + DevRel | Docs engineer | Early adopters |
| Commercial licensing / pricing | Business owner | Product/Legal | Engineering |

Weekly architecture review for API/ADR changes; twice-weekly issue triage during early build; sprint demonstration with correctness + benchmarks; monthly design-partner feedback; release signoff must be independent of benchmark authors where practical.

## 24. Risks, mitigations, and go/no-go decisions

| Risk | Impact / likelihood | Mitigation / decision |
|---|---|---|
| Dapper.AOT is already highly optimized | High / High | Differentiate on schema safety and workflow; publish honest benchmarks |
| Provider Native AOT feature gaps (notably auth) | High / Medium | Matrix-gate OS/driver/auth; exclude unsupported paths explicitly |
| SQL parser breadth explodes | High / High | Dialect-specific supported subset; no false validation; staged grammar expansion |
| Code generator increases compile time | Medium / High | Incremental pipeline, stable cache keys, benchmark 1/100/1000 queries |
| Dynamic fallback undermines AOT promise | High / Medium | Strict generated-only path by default for AOT; explicit dynamic JIT opt-in |
| Mapping optimization causes wrong data | Critical / Medium | Differential correctness tests vs raw provider, strict null/type rules |
| Connection/reader leaks under streaming | Critical / Medium | Fault injection, early-dispose and pool-hygiene tests |
| SQL Server plan instability from inferred types | High / Medium | Native parameter metadata, length/precision tests, diagnostics |
| Observability costs exceed query overhead | High / Medium | Disabled-by-default in Core; bounded opt-in instrumentation |
| AI suggestions change semantics or leak data | Critical / High | Sanitization, sandbox, result equivalence, human approvals |
| Feature creep toward full ORM | High / High | Query-first non-goals and milestone gates enforced by Product Owner |
| Small OSS traction / paid demand | High / High | Early design partners; validate problem/price before Cloud build |
| Trademark/package namespace collision | High / Unknown | Legal trademark, domain and NuGet clearance before public brand launch |
| Multiple packages create maintenance burden | Medium / Medium | Keep package graph minimal, package-wide release cadence, API compatibility tests |

### 24.1 Stop or pivot conditions

- After a substantive optimization cycle, VelQuery remains significantly slower and more allocative than raw provider and Dapper.AOT on **the specific query patterns being targeted**, without offsetting value from reliability/safety.
- The generator cannot be made sufficiently deterministic/incremental without unacceptable build-time degradation.
- Security tests show unresolvable generated-SQL injection or data-corruption risks.
- Design partners consistently prefer existing tools and will not adopt schema validation/performance workflows; reconsider the query engine scope versus a standalone analyzer.

## 25. Product scope and commercial architecture

| Offering | What it contains | Proposed license and monetization | Condition |
|---|---|---|---|
| VelQuery Core | SQL execution, typed mapping, generators, essential analyzers | Apache 2.0 open source | No gated fast path or network license check |
| VelQuery Query Tooling | Advanced offline schema and performance checks, IDE integration | Free baseline + possible paid Studio | Must earn productivity value |
| VelQuery AI Studio | AI SQL review, plan explanation, measured optimization, guided PRs | Developer/team subscription | Model costs and data policies visible |
| VelQuery Cloud | Multi-project trends, query regression history, dashboards and team collaboration | SaaS subscription | Core remains independent |
| VelQuery Enterprise | Private deployment, advanced governance, support, policies, approvals | Commercial contracts | Separate operational/security review |

**Monetization hypothesis:** developers will pay for fewer database incidents, reduced debugging effort and trusted AI-assisted review—not for unlocking ordinary SQL performance. Validate pricing with design partners; do not assume price points from competitor listings guarantee conversion. Core Apache 2.0 is a recommendation subject to legal review and third-party attribution. VelQuery branding is provisional until trademark/NuGet/domain clearance.

## 26. Open questions, assigned decisions, and default assumptions

| Decision ID | Question | Proposed default | Owner / due gate |
|---|---|---|---|
| D-01 | Public entry point: extension methods vs session API? | `IVelQuerySession` with generated query static method | Architect / Gate A |
| D-02 | Should dynamic SQL exist in Core? | Yes in opt-in JIT compatibility package; not generated path | Architect / Gate A |
| D-03 | SQL Server AOT GA? | Only after supported driver/auth matrix passes | Provider lead / Gate C |
| D-04 | PostgreSQL included in v1.0 GA? | Ship as supported only if parity gates pass; else preview | PO / Gate C |
| D-05 | Schema snapshot P0 or P1? | P1 after robust query semantics | PO / Gate A |
| D-06 | Type conversion strictness? | Strict generated default; type handlers explicit | Tech Lead / Gate B |
| D-07 | Telemetry implementation? | Optional extension with OTel conventions and data minimization | Security/Observability / Gate C |
| D-08 | Streaming results ownership pattern? | Enumerator owns reader until disposal; caller may own connection | Tech Lead / Gate B |
| D-09 | Which SQL dialect grammar subset? | Most used static SELECT + parameter cases, documented coverage | Compiler lead / Gate B |
| D-10 | Paid feature boundary? | Keep core speed and critical safety free | Product/Legal / before beta |
| D-11 | Prepared command policy? | Driver-native, opt-in until benchmarked | Provider lead / Gate C |
| D-12 | AI model hosting default? | Metadata-only, pluggable providers, private deployments later | Security/Product / AI beta |

## 27. ADR checklist for implementation kickoff

Create short Architecture Decision Records before merging foundational modules:

- **ADR-0001:** Query-first scope and execution/compile-time/AI dependency isolation.
- **ADR-0002:** Session and connection/transaction ownership and streaming disposal.
- **ADR-0003:** Generated typed query contracts vs dynamic SQL fallback.
- **ADR-0004:** Mapping semantics: constructor binding, NULLs, enum and column naming.
- **ADR-0005:** Parameter metadata, provider types, SQL Server and PostgreSQL adapters.
- **ADR-0006:** Static SQL validation subset and offline schema snapshot canonicalization.
- **ADR-0007:** Native AOT provider/auth compatibility matrix.
- **ADR-0008:** Benchmark fairness, regression gating and publishing of failures.
- **ADR-0009:** Optional diagnostics privacy and backpressure policy.
- **ADR-0010:** No implicit write retry, transaction safety and partial streaming behavior.
- **ADR-0011:** AI tool permissions, sanitized evidence and human approval gate.
- **ADR-0012:** Versioning, Core open-source boundary and commercial packaging.

## 28. External evidence, references and research notes

**Research snapshot:** 10 October 2026. Sources support platform assumptions and competitor context; they are **not** evidence of VelQuery product performance. Consult latest docs and package changelogs before implementing any version-specific behavior.

- **[R1] Dapper.AOT Getting Started:** https://aot.dapperlib.dev/gettingstarted.html — build-time generation, limitations of supported API patterns.
- **[R2] Dapper.AOT FAQ:** https://aot.dapperlib.dev/faq — generator/analyzer and interceptor details.
- **[R3] EF Core advanced performance:** https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics — compiled queries, caching and fair measurement considerations.
- **[R4] Npgsql v8 Native AOT notes:** https://www.npgsql.org/doc/release-notes/8.0.html — AOT/trimming support and exclusions.
- **[R5] SqlClient support lifecycle:** https://learn.microsoft.com/en-us/sql/connect/ado-net/sqlclient-driver-support-lifecycle — supported driver lines and compatibility.
- **[R6] SqlClient AOT authentication regression issue:** https://github.com/dotnet/SqlClient/issues/4193 — provider-auth feature is not automatically AOT safe.
- **[R7] Roslyn incremental generator design:** https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md — efficient, incremental code generation concepts.
- **[R8] .NET lifecycle:** https://learn.microsoft.com/en-us/dotnet/core/releases-and-support — .NET 10 LTS lifecycle.
- **[R9] .NET 10 runtime performance:** https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/ — runtime and Native AOT context.
- **[R10] OWASP SQL Injection Prevention:** https://cheatsheetseries.owasp.org/cheatsheets/SQL_Injection_Prevention_Cheat_Sheet.html — parameter binding, allowlist and least privilege.
- **[R11] OWASP LLM Prompt Injection:** https://cheatsheetseries.owasp.org/cheatsheets/LLM_Prompt_Injection_Prevention_Cheat_Sheet.html — AI tool safety and untrusted model input.
- **[R12] OpenTelemetry SQL semantic conventions:** https://opentelemetry.io/docs/specs/semconv/db/sql/ — SQL spans, summaries, query text and parameter privacy.
- **[R13] Npgsql auto prepare settings:** https://github.com/npgsql/doc/blob/main/conceptual/Npgsql/connection-string-parameters.md — provider-managed prepared-statement configuration.
- **[R14] EF Core efficient querying:** https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying — query shape and related data performance trade-offs.

## 29. Product Owner handoff: the first development instruction

> **Build VelQuery Core, not VelQuery Cloud.** Start with a .NET 10 SQL Server query engine whose semantics are correct and whose performance is compared transparently with raw ADO.NET and Dapper/Dapper.AOT. Implement resource-safe async query, scalar, command, transaction and streaming operations, typed parameters, and a first source-generated mapping path. Make unsupported AOT/dynamic behavior explicit; never compromise correctness for microbenchmarks. Document the API, write failing correctness tests first, collect baseline measurements, and advance through the gates in Section 22. Do not implement AI or a web dashboard in the runtime hot path. Prepare metadata/query ID contracts so Studio can add AI-assisted analysis later without coupling normal SQL execution to any external service.

### Kickoff artifacts to produce immediately

1. `docs/architecture/adr/ADR-0001` through `ADR-0005` as proposed decisions awaiting review.
2. A small SQL Server test fixture with 1/100/100K rows and NULL/Unicode/decimal edge cases.
3. A raw ADO.NET vs Dapper vs Dapper.AOT benchmark suite **before** implementing the optimized VelQuery mapper.
4. Public API sketch with cardinality, transaction and connection ownership semantics.
5. `VelQuery.Core` and `VelQuery.SqlServer` skeleton with lifecycle and cancellation tests.
6. First generated query prototype and deterministic generated-source golden test.
7. `provider-compatibility.md` tracking successful/unsupported Native AOT and authentication combinations.

**End of PRD v1.0.** Product is approved to start architectural spikes and the query-engine foundation subject to explicit engineering kickoff review; it is **not** represented as production-ready software today.
