# ADR-0015: Multi-database support strategy

- **Status:** Proposed
- **Date:** 2026-10-11
- **Amends:** ADR-0001 (query-first scope); see [PRD amendment v1.1](../../prd/VelQuery_PRD_v1.1_Amendment.md)

## Context

The product direction now includes relational, distributed SQL, analytics, document, key-value, wide-column, graph,
time-series, search and vector databases. Today's architecture assumes SQL text over ADO.NET: `QueryDescriptor`
carries SQL, binders write `DbParameter`s, mappers read `DbDataReader`, sessions own `DbConnection`/`DbTransaction`,
and analyzers scan SQL. Two providers exist (SQL Server, PostgreSQL), both ADO.NET.

Most analytics, distributed-SQL, SQL time-series and SQL vector engines ship ADO.NET drivers and speak SQL dialects,
so they fit the current design. Document, graph, key-value, wide-column and search databases do not: they use native
drivers, non-SQL query languages, JSON or graph results, and different transaction models.

## Decision

1. **Phase by fit, not by popularity.** Phase 1 covers every SQL-speaking engine with the existing architecture
   (generic ADO.NET provider, then dedicated providers). Non-SQL engines wait for phase 2.
2. **Three support tiers** per database, shown in [database-support.md](../../database-support.md):
   *verified* (integration suite passes on the real engine in CI), *preview* (implemented, suite passes locally or not
   yet in CI), *generic* (works through the generic ADO.NET provider with reduced guarantees).
3. **Engine-neutral core before any non-SQL code (phase 2).** Extract the parts of `VelQuerySession` that are not
   ADO.NET-specific — lifetime ownership, cancellation, exception translation, query identity, streaming disposal,
   strict mapping rules — into an engine contract. The ADO.NET engine becomes one implementation; SQL users see no API
   or behavior change, and benchmarks must show no hot-path regression.
4. **One pilot non-relational adapter (phase 3)** proves the engine contract end to end (generated typed contracts,
   language-specific compile-time checks, mapping, streaming, errors, tests, benchmarks) before more are added. The
   pilot database is **MongoDB** (decision D-13, 2026-10-19).
5. **Same guarantees everywhere.** Every adapter keeps explicit ownership, cancellation, no hidden retries, strict
   typed mapping, value-free errors and no AI on the request path. A database that cannot meet one of these documents
   the gap in its matrix row instead of weakening the rule globally.
6. **One package per database** (`VelQuery.MySql`, `VelQuery.Sqlite`, `VelQuery.MongoDB`, …) depending on Core and the
   official or most-maintained driver; Core never references a database driver.

## Consequences

- Phase 1 is incremental and low-risk: providers reuse descriptors, generated mappers, bulk patterns and test suites.
- Phase 2 is a real refactor of Core and must be reviewed as an API-stability risk before v1.0 API freeze (PRD §8).
- Compile-time checks will differ in depth per language; parameter/placeholder checks come first (VQ-DB-006).
- CI cost grows with every engine (containers, cloud emulators, credentials for managed services such as Snowflake and
  BigQuery that have no local container).
- Marketing and docs must state tiers honestly; "supports N databases" counts verified ones only.

## Alternatives considered

- **Stay SQL-only (v1.0 scope).** Lowest risk and deepest quality, but does not meet the new product direction.
- **One universal query API/LINQ over every database.** Rejected: lowest-common-denominator semantics, hides each
  engine's strengths, and contradicts the query-first principle.
- **Wrap each native driver ad hoc without a shared engine contract.** Faster to start, but guarantees (ownership,
  errors, cancellation) would drift per adapter.

## Open questions

- ~~D-13: phase-3 pilot database~~ — decided 2026-10-19: MongoDB (most-used document database, local multi-arch container for CI, a rich query language (filters, aggregation) that best exercises typed contracts and compile-time checks, and real multi-document transactions).
- Shape of non-SQL contracts: `[VelQueryMongo]`-style attributes per language, or one attribute with a language field.
- Transaction semantics for engines with limited or no multi-statement transactions (Cassandra, Redis, DynamoDB).
- How managed-only services (Snowflake, BigQuery, Cosmos DB) are tested in CI (emulators vs. sponsored accounts).
