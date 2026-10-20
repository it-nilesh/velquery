# Database support

The single list of databases VelQuery supports or plans to support, by database type. Direction and phases:
[PRD amendment v1.1](prd/VelQuery_PRD_v1.1_Amendment.md) and [ADR-0015](architecture/adr/ADR-0015-multi-database-support-strategy.md).
Per-feature verification details for implemented providers live in [provider-compatibility.md](provider-compatibility.md).

Last updated: 2026-10-20.

## Status tiers

| Status | Meaning |
|---|---|
| **Verified** | Provider implemented and its integration suite passes on the real engine **in CI** |
| **Preview** | Provider implemented; integration suite passes locally, not yet in CI |
| **Generic** | Works through the planned generic ADO.NET provider with reduced guarantees (no timeout classification, value normalization or bulk path); not separately tested |
| **Planned (phase N)** | On the roadmap; nothing implemented |
| **By demand** | Considered only if design partners need it |
| **Not planned** | Out of scope for now |

No database is **Verified** yet: CI does not exist yet (phase 1 work item).

## Relational and SQL-speaking engines (phase 1 — current architecture)

| Type | Database | Status | Package | Candidate driver | Notes |
|---|---|---|---|---|---|
| Relational | **SQL Server** 2019/2022, Azure SQL | **Preview** | `VelQuery.SqlServer` | Microsoft.Data.SqlClient | 125 integration tests pass locally (on Azure SQL Edge, ARM64). Bulk: SqlBulkCopy. Needs CI on SQL Server 2022 x64 and Entra ID auth |
| Relational | **PostgreSQL** 17 | **Preview** | `VelQuery.PostgreSQL` | Npgsql | 31 integration tests pass locally on PostgreSQL 17. Bulk: binary COPY |
| Relational | **MySQL** 8.4 / **MariaDB** 11.8 | **Preview** | `VelQuery.MySql` | MySqlConnector 2.6.2 | 58 integration tests pass locally on `mysql:8.4` and `mariadb:11` (ARM64). Bulk: `MySqlBulkCopy` (LOAD DATA LOCAL; needs `AllowLoadLocalInfile` + `local_infile=ON`), all-or-nothing with skipped-row/warning detection. MySQL-aware build-time SQL scanning |
| Relational / embedded | **SQLite** 3.53 | **Preview** | `VelQuery.Sqlite` | Microsoft.Data.Sqlite 10.0.13 | 70 integration tests pass locally (no Docker). Bulk: one transaction + prepared INSERT (~1.4M rows/s). NativeConversions for TEXT dates/decimals/Guids; cancellation interrupts running statements (`sqlite3_interrupt`) |
| Relational | Oracle | Planned (phase 1, later) | `VelQuery.Oracle` | Oracle.ManagedDataAccess.Core | `:name` placeholders; array binding for bulk |
| Relational | IBM DB2 | By demand | — | IBM's .NET provider | |
| Distributed SQL | CockroachDB, YugabyteDB | Planned (phase 1) | `VelQuery.PostgreSQL` | Npgsql | PostgreSQL-compatible with differences (transactions, retries); needs own test runs |
| Distributed SQL | TiDB | Planned (phase 1) | `VelQuery.MySql` | MySqlConnector | MySQL-compatible |
| Distributed SQL | Google Spanner | By demand | — | Google's ADO.NET provider | Managed only |
| Analytics / embedded | DuckDB | Planned (phase 1) | `VelQuery.DuckDb` | DuckDB.NET | Bulk: Appender |
| Analytics | ClickHouse | Planned (phase 1) | `VelQuery.ClickHouse` | ClickHouse ADO.NET client | Bulk: native bulk copy |
| Analytics | Snowflake | Planned (phase 1) | `VelQuery.Snowflake` | Snowflake.Data | Managed only: CI needs an account |
| Analytics | BigQuery | Planned (phase 1) | `VelQuery.BigQuery` | Google BigQuery client (not ADO.NET) | Needs phase-2 engine contract or an ADO.NET bridge |
| Analytics | Amazon Redshift | Planned (phase 1) | `VelQuery.PostgreSQL` or own | Npgsql (PostgreSQL protocol) | Dialect differences to verify |
| Analytics / lakehouse | Databricks SQL, Trino/Presto, Athena, Synapse | By demand | — | ODBC / vendor clients | |
| Time-series (SQL) | TimescaleDB | Generic → Planned (phase 1) | `VelQuery.PostgreSQL` | Npgsql | PostgreSQL extension; works through the PostgreSQL provider, not separately tested |
| Time-series (SQL) | QuestDB | By demand | — | Npgsql (PG wire) | |
| Vector (SQL) | pgvector | Planned (phase 1) | `VelQuery.PostgreSQL` | Npgsql + pgvector plugin | Needs vector type mapping |
| Vector (SQL) | SQL Server vector type | Planned (phase 1) | `VelQuery.SqlServer` | Microsoft.Data.SqlClient | |
| Spatial | PostGIS | By demand | `VelQuery.PostgreSQL` | Npgsql + NetTopologySuite | |
| Any other ADO.NET database | — | **Generic** | `VelQuery.Core` (`VelQueryAdoNet.CreateDataSource`) | Vendor ADO.NET driver | Reduced guarantees (VQ-DB-003); exercised end to end with Microsoft.Data.Sqlite (8 tests) |

## Non-relational engines (phases 3–4 — need the phase-2 engine-neutral core)

| Type | Database | Status | Candidate driver | Query surface |
|---|---|---|---|---|
| Document | **MongoDB** 8 | **Preview** | MongoDB.Driver 3.12 (`VelQuery.MongoDB`) | Typed find/count/aggregate/update/delete/insert contracts with VQ2xxx build-time checks (JSON, placeholders, NoSQL-injection rules); generated reflection-free BSON serializers; transactions via driver sessions; ordered bulk insert (not atomic outside a transaction). 79 integration tests pass locally on a mongo:8 replica set. Gaps: client-side write timeouts only; driver retries commit/abort once; driver not AOT-safe. ADR-0019 |
| Document | Azure Cosmos DB (NoSQL API) | Planned (phase 4) | Microsoft.Azure.Cosmos | Cosmos SQL-like queries |
| Document | Couchbase | Planned (phase 4) | Couchbase .NET SDK | SQL++ (N1QL) |
| Document | RavenDB | By demand | RavenDB.Client | RQL |
| Graph | Neo4j | Planned (phase 4) | Neo4j.Driver | Cypher |
| Graph | Amazon Neptune, Cosmos DB Gremlin | Planned (phase 4) | Gremlin.Net | Gremlin (openCypher on Neptune) |
| Graph | Memgraph | By demand | Neo4j.Driver (Bolt) | Cypher |
| Key-value | Redis | Planned (phase 4) | StackExchange.Redis | Commands |
| Key-value | Amazon DynamoDB | Planned (phase 4) | AWS SDK | API / PartiQL |
| Wide-column | Apache Cassandra, ScyllaDB | Planned (phase 4) | DataStax C# driver | CQL |
| Search | Elasticsearch, OpenSearch | Planned (phase 4) | Official .NET clients | JSON query DSL |
| Search | Azure AI Search | By demand | Azure SDK | REST/SDK |
| Vector (native) | Qdrant, Pinecone, Weaviate, Milvus | Planned (phase 4) | Vendor clients | APIs |
| Time-series | InfluxDB, Prometheus | By demand | Vendor clients | Flux/SQL, PromQL |
| Multi-model | ArangoDB, SurrealDB | By demand | Vendor clients | AQL, SurrealQL |
| Event store / streaming | EventStoreDB, ksqlDB, Materialize | Not planned | — | — |

Phase-3 pilot (decision **D-13**, decided 2026-10-19): **MongoDB** — most-used document database, local multi-arch container for CI, a rich query language (filters, aggregation) that best exercises typed contracts and compile-time checks, and real multi-document transactions. Cosmos DB and Redis follow in phase 4.

## Definition of "supported"

A database moves to **Verified** only when all of these hold (PRD amendment VQ-DB-001/002):

1. Dedicated package with provider/adapter, data-source factory and DI registration.
2. Integration suite on the real engine (or official emulator) running in CI: cardinality, types, parameters and
   injection, timeouts/cancellation, streaming and early exit, pool hygiene, transactions, generated contracts.
3. Native bulk path where the engine has one, all-or-nothing by default where possible.
4. Row in [provider-compatibility.md](provider-compatibility.md) with versions and evidence.
5. Benchmark rows against the native driver and the most common .NET alternative, losses included.
