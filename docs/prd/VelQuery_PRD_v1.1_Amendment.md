# VelQuery — PRD Amendment v1.1: Multi-Database Support

**Amends:** [VelQuery_PRD_v1.0.md](VelQuery_PRD_v1.0.md) | **Date:** 11 October 2026 | **Status:** Proposed
**Decision record:** [ADR-0015](../architecture/adr/ADR-0015-multi-database-support-strategy.md) | **Tracker:** [database-support.md](../database-support.md)

> v1.0 remains the baseline. This amendment changes only the sections listed below; everything else in v1.0 still
> applies. Like v1.0, it states intent and targets, not shipped capability.

## 1. Summary of change

v1.0 scoped VelQuery as a query-first **SQL** engine for SQL Server then PostgreSQL, and listed "all DB providers
simultaneously" as a non-goal (§2.3). The product direction is now **broad database coverage across database
types** — relational, distributed SQL, analytics, document, key-value, wide-column, graph, time-series, search and
vector — delivered in **phases**, each behind its own quality gate.

What does **not** change: correctness before speed, no hidden retries, strict mapping, explicit ownership, no AI on the
request path, published benchmarks including losses, and "verified only when an automated test passed".

## 2. Database types in scope

| Type | Representative databases | Query surface | Phase |
|---|---|---|---|
| Relational (OLTP) | SQL Server, PostgreSQL, MySQL/MariaDB, SQLite, Oracle, DB2 | SQL | 1 |
| Distributed SQL / NewSQL | CockroachDB, YugabyteDB, TiDB, Spanner, Aurora DSQL | SQL dialects | 1 |
| Analytics / OLAP / warehouse | Snowflake, BigQuery, Redshift, ClickHouse, DuckDB, Databricks SQL, Synapse | SQL dialects | 1 |
| Lakehouse / federated query | Trino/Presto, Athena, Spark SQL | SQL | 1 |
| Time-series (SQL-based) | TimescaleDB, QuestDB | SQL | 1 |
| Vector (SQL-based) | pgvector, SQL Server vector type | SQL + extensions | 1 |
| Document | MongoDB, Azure Cosmos DB, Couchbase, RavenDB | MQL, Cosmos SQL API, SQL++ | 3 (pilot) |
| Graph | Neo4j, Amazon Neptune, Cosmos DB Gremlin, Memgraph | Cypher, Gremlin, SPARQL, GQL | 3–4 |
| Key-value | Redis, DynamoDB | Commands / APIs | 4 |
| Wide-column | Cassandra, ScyllaDB | CQL | 4 |
| Search | Elasticsearch, OpenSearch, Azure AI Search | JSON query DSL | 4 |
| Vector (native) | Qdrant, Pinecone, Weaviate, Milvus | APIs | 4 |
| Time-series (non-SQL) | InfluxDB, Prometheus | Flux, PromQL | 4 (by demand) |
| Multi-model | ArangoDB, SurrealDB | AQL, SurrealQL | 4 (by demand) |
| Event stores / streaming | EventStoreDB, ksqlDB, Materialize | Streams / SQL | Not planned (revisit with demand) |

## 3. Phases and gates

| Phase | Scope | Exit gate |
|---|---|---|
| **1. SQL everywhere** | Finish SQL Server and PostgreSQL (CI on SQL Server 2022 + PostgreSQL, Native AOT matrix, cloud auth). Generic ADO.NET provider (reduced guarantees). Dedicated providers: MySQL/MariaDB (MySqlConnector), SQLite (Microsoft.Data.Sqlite), then analytics engines (DuckDB, ClickHouse, Snowflake, BigQuery) and distributed SQL. | Per provider: integration suite on the real engine in CI, compatibility-matrix row, bulk path where the engine has one, benchmark rows |
| **2. Engine-neutral core** | Split Core into an engine-neutral execution contract (execute, stream, map, ownership, cancellation, errors) with ADO.NET as one implementation. No behavior change for SQL users. | ADR approved; SQL suites unchanged and green; no hot-path regression in benchmarks |
| **3. Non-relational pilot** | One adapter end to end: generated typed contracts, compile-time checks for that query language, mapping, streaming, error contract, tests and benchmarks against the native driver. Pilot database: **open (D-13)**. | Pilot passes its parity gate and a design partner validates it |
| **4. Expand by demand** | Further document, graph, key-value, wide-column, search and vector adapters | Each adapter passes the same gate as phase 3 |

Phase 1 continues to deliver value on its own; phases 3–4 start only after phase 2 is approved.

## 4. Requirements added

| ID | Requirement | Priority |
|---|---|---|
| VQ-DB-001 | Every supported database has a row in [database-support.md](../database-support.md) and, once implemented, in the provider-compatibility matrix with verified/unverified status | P0 |
| VQ-DB-002 | A database is "supported" only after its integration suite passes on the real engine in CI; until then it is "preview" | P0 |
| VQ-DB-003 | A generic ADO.NET provider accepts any `DbConnection` with documented reduced guarantees (no timeout classification, no value normalization, no bulk path) | P1 |
| VQ-DB-004 | Provider-specific bulk ingestion uses the engine's native path (SqlBulkCopy, COPY, LOAD DATA, Appender, …) with all-or-nothing behavior by default where the engine allows it | P1 |
| VQ-DB-005 | Non-relational adapters keep the cross-cutting guarantees: explicit ownership, cancellation, no hidden retries, strict typed mapping, value-free errors, no AI on the request path | P0 (phase 3+) |
| VQ-DB-006 | Each non-relational query language gets compile-time contract checks equivalent in spirit to VQ1001/VQ1002 (parameters/placeholders), or documents why it cannot | P1 (phase 3+) |
| VQ-DB-007 | Benchmarks compare each adapter with the native driver and the most common .NET alternative for that database, publishing losses | P1 |
| VQ-DB-008 | The SQL scanner becomes dialect-aware (dollar quotes, backticks, `E''` strings, `ARRAY[...]`, `:name`/`?` placeholders) before a dialect is declared supported | P1 |

## 5. Changes to v1.0 sections

- **§2.3 Non-goals:** "all DB providers simultaneously" becomes "all databases **at once**": coverage is phased and
  gated. Change tracking, migrations, LINQ translation and automatic sharding remain non-goals.
- **§3 Architecture:** E1 (Execution Engine) gains an engine-neutral contract in phase 2; ADO.NET-specific code moves
  behind it. E2 (Query Compiler) gains per-language analyzers in phase 3+.
- **§10 Database support:** the SQL Server/PostgreSQL matrix is extended by [database-support.md](../database-support.md).
- **§19 Repository structure:** one package per database (`VelQuery.<Database>`), plus engine packages as needed.
- **§20 Epics:** EP-15 generic ADO.NET provider; EP-16 MySQL/MariaDB; EP-17 SQLite; EP-18 analytics providers;
  EP-19 engine-neutral core; EP-20 non-relational pilot.
- **§26 Decisions:** D-13 phase-3 pilot database (MongoDB, Cosmos DB, Neo4j or Redis) — open, owner: Product Owner,
  due before phase 3 starts. D-14 support tiers (verified / preview / generic) — proposed as in VQ-DB-001..003.

## 6. Risks

| Risk | Impact / likelihood | Mitigation |
|---|---|---|
| Breadth dilutes depth ("universal data access" libraries historically end up shallow) | High / High | Phase gates; a database is listed as supported only after its suite passes; generic tier clearly labeled |
| Non-SQL databases do not fit the SQL/ADO.NET core | High / Certain | Phase 2 engine-neutral contract before any non-relational code |
| Maintenance cost grows per database (drivers, CI containers, auth modes) | High / High | Demand-driven expansion; shared conformance test kit per engine type |
| Query-language analyzers multiply compiler work | Medium / High | Start with placeholder/parameter checks only (VQ-DB-006) |
| Performance claims across very different engines become misleading | Medium / Medium | Benchmarks per database against its native driver only; no cross-engine comparisons |
