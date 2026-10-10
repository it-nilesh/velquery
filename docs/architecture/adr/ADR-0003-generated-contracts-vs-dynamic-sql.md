# ADR-0003: Generated typed query contracts vs runtime SQL

- **Status:** Proposed
- **Date:** 2026-10-10
- **Deciders:** Principal Architect, Compiler lead
- **PRD references:** D-02, §8 (API design caveat), VQ-FR-005, -018..-020, -031, §9.5, §11.3, VQ1007
- **Code:** `src/VelQuery.Abstractions/QueryDescriptor.cs`, `Mapping/RowMappers.cs`, `Mapping/ScalarRowMapperFactory.cs`,
  `src/VelQuery.Core/Api/IVelQuerySession.cs`, `Api/VelQueryTextCommand.cs`, `Parameters/QueryParameters.cs`,
  `Mapping/ReflectionRowMapperFactory.cs`, `Execution/QueryIdentity.cs`

## Context

A call such as `session.QueryAsync<UserRow>(sql, new { Id = 42 })` cannot be reflection-free by itself: the runtime
sees only a string and an object (PRD §8 caveat). Yet runtime SQL is unavoidable (dynamic filters, stored procedure
calls, migration from Dapper). We need one fast, AOT-safe path that the generator targets, and a runtime path whose
costs and guarantees are explicit.

## Decision

**1. Generated/compiled path: `QueryDescriptor`.** `QueryDescriptor<TArgs>` (ID, SQL, `CommandType`, optional
`CommandTimeout`, `ParameterBinder<TArgs>` delegate) and `QueryDescriptor<TArgs,TRow>` (adds an
`IRowMapperFactory<TRow>`) are immutable and shared across threads. `IVelQuerySession` has descriptor overloads for
every operation. The binder writes typed parameters directly; the mapper factory resolves ordinals per reader shape
(ADR-0004). No reflection, no dictionary lookups on this path. Descriptors may also be hand-written.

**2. Mapper registry: `RowMappers<T>`.** A static generic class holding one `IRowMapperFactory<T>` in a static field
(`Volatile` read/write; last registration wins). Lookup is a static field read, so it is trimming- and AOT-safe and
needs no `Type`-keyed dictionary. Built-in scalar types are pre-registered with `ScalarRowMapperFactory<T>`. The
generator registers each generated factory from a `[ModuleInitializer]`, so runtime-SQL calls for a generated row
type automatically use generated code.

**3. Runtime SQL path.** String overloads (`QueryAsync<T>(sql, QueryParameters?)`, …, and `session.Sql(sql,
parameters)` for per-call timeout, query ID and stored procedures) require **explicit `QueryParameters`** — there is no
`object param` overload. Anonymous objects are accepted only through `QueryParameters.FromObject<T>(args)`, whose
`T` is annotated `[DynamicallyAccessedMembers(PublicProperties)]`: trimming-safe, AOT-compatible (no emit), but it
does use reflection and is not for hot paths. Runtime queries without `WithQueryId` get a value-free identity
`sql:<FNV-1a 64-bit hash of the SQL text>`.

**4. Mapper resolution for runtime SQL:** `RowMappers<T>.Factory` → if `VelQueryOptions.AllowDynamicFallback`
(default **false**) the reflection mapper → otherwise `VelQueryUnsupportedCapabilityException` naming the three
fixes. The reflection mapper is internal, invokes constructors/setters via reflection (never `Reflection.Emit`), caches
an immutable per-type plan, and its `T` is annotated for the trimmer. It follows the same rules as generated mappers
(ADR-0004).

**5. Scalar semantics (`ExecuteScalarAsync`).** No rows → `default` for targets that accept null, otherwise
`VelQueryNoRowsException`. First cell `DBNull` → `default` for nullable targets; for non-nullable targets a
`VelQueryMappingException` in strict mode (default) or `default` when `StrictMapping = false`. Values go through
`ValueConverter` (lossless only).

**6. Generated `[VelQuerySql]` partial methods** (generator built in parallel; this is the decided rule). The
operation is chosen from the declared return type plus an optional explicit `Cardinality`:
`Task<IReadOnlyList<T>>` → `QueryAsync`; `IAsyncEnumerable<T>` → `StreamAsync`; `Task<T?>` → `QuerySingleOrDefault`;
`Task<T>` → `QuerySingle`. `Cardinality` overrides the default (e.g. `First`/`FirstOrDefault`) — the generator never
silently picks "first" for a single-row signature, because `Single` is the only choice that detects a wrong
`WHERE` clause.

## Consequences

- One AOT-safe execution core serves both paths; the runtime path costs one registry read plus whatever
  `QueryParameters` allocates (a list and one entry per parameter).
- Migration from Dapper requires rewriting `new { ... }` arguments (or wrapping them in `FromObject`) and registering
  or generating mappers; `AllowDynamicFallback = true` is the JIT-mode escape hatch.
- Because generated factories register globally, any assembly can replace the mapper for a type (last wins). This is
  simple and fast but makes duplicate registrations a silent override.
- Without a generated mapper and with fallback off, failures are runtime exceptions, not build errors, until analyzer
  VQ1007 exists.

## Alternatives considered

- **`object param` overloads with runtime reflection (Dapper model).** Familiar, but hides reflection on the hot path
  and cannot be AOT-safe without interception. Rejected; `FromObject` makes the cost visible.
- **`Dictionary<Type, object>` mapper registry.** Needs casts and locking or a frozen dictionary; static generic field
  is cheaper and AOT-friendly. Rejected.
- **Reflection.Emit/expression-compiled mappers for the fallback.** Faster than invocation in JIT mode but breaks
  Native AOT and the "no emit" rule. Rejected.
- **C# interceptors for runtime-SQL call sites** — see open question 1.

## Open questions

1. **Interceptors (not adopted in v0.1).** Dapper.AOT rewrites ordinary `connection.QueryAsync<T>(sql, args)` call
   sites with C# interceptors; VelQuery could do the same for `session.QueryAsync<T>(constSql, ...)`.
   *Pros:* Dapper-like ergonomics with generated binders/mappers, smaller migration diff, constant SQL at the call site
   becomes analyzable. *Cons:* the consuming project must opt in (`InterceptorsNamespaces`), generated code is bound
   to source locations (the Dapper.AOT 1.1.0 output in `benchmarks/` uses file path + line/column and suppresses
   `CS9270`, an SDK-dependent change), the call site reads like runtime SQL but runs generated code (harder to debug
   and review), only constant SQL qualifies so behaviour changes silently when SQL becomes dynamic, and there is
   extra incremental-build work at every call site. Decide after the generator prototype and build-time budget
   (VQ-NFR-007) are measured. (Compiler lead, Gate C)
2. Should duplicate `RowMappers<T>.Register` calls with different factories be detected (debug assert or
   diagnostic)? (Tech Lead, Gate B)
3. Generator return-type rules for commands (`Task<int>` affected rows vs a scalar `int`) and multi-result contracts
   need an explicit `Cardinality`/kind; confirm with the generator implementation. (Compiler lead, Gate B)
4. Descriptors have no output-parameter hook (`DescriptorSpec.Complete` is a no-op), so output parameters work only
   with `QueryParameters` today. (Compiler lead, Gate B)
