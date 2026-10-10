# ADR-0001: Query-first scope and dependency isolation

- **Status:** Proposed
- **Date:** 2026-10-10
- **Deciders:** Principal Architect, Product Owner, Engineering Lead
- **PRD references:** §0, §2.3, §3 (architectural rule), §4.1, §15.3 (VQ-AI-011), §19.1, VQ-FR-030, VQ-FR-035
- **Code:** `src/VelQuery.Abstractions/*.csproj`, `src/VelQuery.Core/*.csproj`, `src/VelQuery.SqlServer/*.csproj`

## Context

VelQuery's value depends on two properties that are easy to erode: the execution path stays small and predictable,
and Core can be adopted without pulling in an ORM, a telemetry stack, a cloud service or an AI client. The PRD
(§3) splits the product into six engines and states that E1 (execution) is a leaf runtime, E2 (compiler) runs only
at build time, and E3–E6 build on E1's public contracts rather than the other way round. We need that split to be
enforced by project references, not by convention.

## Decision

1. **Scope.** Core is a query-first execution engine: execute, map, stream, multi-result, transactions, typed
   parameters, error taxonomy. Change tracking, LINQ translation, migrations, bulk frameworks, sharding, result
   caching and identity maps are out of scope (§2.3). Fluent/CRUD "data engine" features may only arrive as separate
   packages that depend on Core.
2. **Dependency graph (as implemented).**

   | Package | Allowed references | Today |
   |---|---|---|
   | `VelQuery.Abstractions` | BCL only | none (net10.0, `IsAotCompatible`) |
   | `VelQuery.Core` | Abstractions + BCL/ADO.NET (`System.Data.Common`) | Abstractions only; uses BCL `ColumnAttribute` |
   | `VelQuery.SqlServer` | Core, Abstractions, its official driver | Core + `Microsoft.Data.SqlClient` 7.1.1 + `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.12 (for `AddVelQuerySqlServer`) |
   | `VelQuery.Generators`, `VelQuery.Analyzers` | Roslyn only (netstandard2.0) | being built; consumed as analyzers (`PrivateAssets=all`), never a runtime dependency |
   | Future `VelQuery.Diagnostics.*`, AI, Studio, Cloud | Core (never the reverse) | not started |

   Driver types never appear in Abstractions or Core signatures; provider-specific metadata travels as
   `ParameterSpec.ProviderDbType` (an `int`) and is interpreted by the provider (ADR-0005).
3. **Runtime libraries are trimming/AOT-annotated** (`IsAotCompatible=true`, trim/AOT warnings are errors). No
   `Reflection.Emit`, `MakeGenericType`/`MakeGenericMethod` or `Convert.ChangeType` on execution paths. The only
   reflection in Core is the opt-in fallback mapper and `QueryParameters.FromObject`, both annotated with
   `DynamicallyAccessedMembers` (ADR-0003).
4. **Nothing on the hot path that is not the query.** No AI, network (other than the database connection), telemetry,
   logging, `Activity`/`Meter` creation, SQL parsing or license checks during execution. Core currently contains no
   logging or diagnostics hooks at all; when diagnostics arrive they live in an optional package that Core does not
   reference, and the disabled path must allocate nothing (VQ-NFR-010).
5. **Build-time isolation.** Generators and analyzers never open a database connection, touch the network, read
   secrets or execute user code (VQ-FR-035). Schema validation, when it arrives, reads an offline snapshot file.

## Consequences

- Core can be used in Native AOT and trimmed apps without dragging in unrelated packages; provider packages carry the
  driver's own AOT caveats (tracked in `docs/provider-compatibility.md`).
- Every cross-cutting concern (diagnostics, resilience, policy) must be designed as a wrapper or an explicit opt-in
  extension point rather than a hidden hook. This is more work for those features and is intentional.
- The SQL Server package currently forces a dependency on `Microsoft.Extensions.DependencyInjection.Abstractions`
  even for apps that do not use DI. It is a small, ubiquitous package, but it is a dependency Core itself avoids.
- Dependency rules are reviewed manually today. A build-time check (e.g. an architecture test asserting the reference
  graph of each assembly) is needed before Gate B.

## Alternatives considered

- **Single package (Core + SqlServer + DI).** Simpler to install, but couples Core's release cadence and AOT story to
  one driver and makes PostgreSQL a breaking reshuffle. Rejected.
- **Built-in `ILogger`/OpenTelemetry in Core.** Convenient, but violates VQ-NFR-010 and the "no mandatory
  OpenTelemetry" rule (§19.1). Rejected in favour of an optional diagnostics package.
- **Generator that queries the database at build time** (live schema). Rejected by VQ-FR-035: builds must be
  reproducible and must not need credentials.

## Open questions

- Should DI registration move to a separate `VelQuery.SqlServer.DependencyInjection` (or a generic
  `VelQuery.Extensions.DependencyInjection`) package so provider packages are pure leaves? (Architect, Gate A)
- Add an automated dependency-graph test (assembly reference allowlist per package) to CI. (Tech Lead, Gate B)
- Package granularity (PRD §19.1 rule 6): is `Abstractions` separate from `Core` worth the extra package once the
  generator's runtime surface is known? (Architect, before beta)
