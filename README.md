# VelQuery

Query-first data access for .NET 10: hand-written SQL with compile-time checks, source-generated typed mapping,
strict runtime semantics and native bulk ingestion. Not an ORM.

> **Status: pre-alpha (preview).** SQL Server, PostgreSQL, MySQL/MariaDB, SQLite and MongoDB are implemented and pass
> their integration suites locally, and Native AOT is verified for SQLite, PostgreSQL and MySQL on osx-arm64. CI is
> configured but has not run (the repository is not hosted yet) and no packages are published. Not yet for production.

## Why VelQuery

- **Mistakes fail the build.** A misspelled `@parameter` is error VQ1001; SQL built by string interpolation is VQ1008.
- **SQL checked against your real schema.** Export a schema snapshot once (`velquery schema snapshot`) and the build
  reports unknown tables and columns, result columns your types need, nullable and type mismatches, and INSERTs that
  miss required columns, with no database connection at build time. See [schema-checked SQL](docs/schema-snapshots.md).
- **Strict at runtime.** NULL into a non-nullable member is an error naming the column; conversions are lossless only;
  no hidden retries; errors never contain parameter or row values.
- **Explicit ownership.** VelQuery never closes your connection or commits your transaction; streams release
  everything on early exit, errors and cancellation.
- **Fast without reflection.** Generated binders and mappers, sequential column reads, pooled buffers.
- **Native bulk paths.** `SqlBulkCopy` for SQL Server and binary `COPY` for PostgreSQL, all-or-nothing by default.

## Quick look

```csharp
// Registration (ASP.NET Core)
builder.Services.AddVelQuerySqlServer(connectionString);   // or AddVelQueryPostgreSql(connectionString)

// A compile-time-checked query; the generator implements the method.
public sealed record Product(int Id, string Name, decimal Price);

[VelQuerySql("products.by-id", "SELECT Id, Name, Price FROM dbo.Products WHERE Id = @Id")]
public static partial class GetProduct
{
    public static partial Task<Product?> ExecuteAsync(IVelQuerySession session, int id, CancellationToken ct = default);
}

var product = await GetProduct.ExecuteAsync(session, 42, ct);

// Runtime SQL with explicit, bound parameters
var cheap = await session.QueryAsync<Product>("SELECT Id, Name, Price FROM dbo.Products WHERE Price < @Max",
                                              new QueryParameters().Add("Max", 10m), ct);

// Bulk insert (SQL Server shown; PostgreSQL uses PostgreSqlBulkInsertPlan)
static readonly SqlBulkInsertPlan<Product> Plan = SqlBulkInsertPlan<Product>.Create("dbo.Products")
    .Column("Name", p => p.Name).Column("Price", p => p.Price);
long written = await session.BulkInsertAsync(Plan, products, ct);
```

Several commands in one round trip, and SQL Server table-valued parameters:

```csharp
IReadOnlyList<int> affected = await session.CreateBatch("orders.close")
    .Add("UPDATE dbo.Orders SET Status = N'closed' WHERE Id = @Id", new QueryParameters().Add("Id", 42))
    .Add("INSERT INTO dbo.Audit (OrderId, Action) VALUES (@Id, N'close')", new QueryParameters().Add("Id", 42))
    .ExecuteAsync(ct);

static readonly SqlTableType<int> IdList = SqlTableType<int>.Create("dbo.IdList").Column("Id", id => id);
var some = await session.QueryAsync<Product>("SELECT p.Id, p.Name, p.Price FROM dbo.Products p JOIN @ids i ON i.Id = p.Id",
                                             new QueryParameters().Add("ids", IdList.Rows(ids)), ct);
```

A complete minimal API with a repository layer is in [samples/QuickStart](samples/QuickStart/README.md).

## Databases

| Database | Status | Package |
|---|---|---|
| SQL Server / Azure SQL | Preview | `VelQuery.SqlServer` |
| PostgreSQL | Preview | `VelQuery.PostgreSQL` |
| MySQL / MariaDB | Preview | `VelQuery.MySql` |
| SQLite | Preview | `VelQuery.Sqlite` |
| MongoDB (document, phase 3 pilot) | Preview | `VelQuery.MongoDB` |
| Any other ADO.NET database | Generic (reduced guarantees) | `VelQuery.Core` (`VelQueryAdoNet`) |
| Oracle, analytics, distributed SQL | Planned (phase 1) | — |
| Cosmos DB, Redis, graph, wide-column, search, vector | Planned (phase 4) | — |

Full list, tiers and phases: [docs/database-support.md](docs/database-support.md). Per-feature evidence:
[docs/provider-compatibility.md](docs/provider-compatibility.md).

## Performance (measured, with caveats)

Measured on a developer laptop against databases in a 2-vCPU Docker VM with BenchmarkDotNet short runs: useful for
comparing libraries on identical work, not as absolute numbers. Raw results and method: [benchmarks/](benchmarks/README.md).

| Case (SQL Server) | VelQuery | Dapper | Dapper.AOT | Raw ADO.NET |
|---|---|---|---|---|
| Allocated, 1,000-row buffered query | 226 KB | 313 KB | 235 KB | 234 KB |
| Allocated, 100k-row stream | 21.75 MB | 29.4 MB | 21.8 MB | 21.8 MB |
| Client CPU, 100k-row stream (vs raw) | 1.05× | 1.14× | 1.06× | 1.00× |
| Latency, small queries | ≈ equal (database round trip dominates) | | | |

| Bulk insert, 1M rows | VelQuery | Native driver | One row per round trip |
|---|---|---|---|
| SQL Server | 2.25 s | 2.38 s (SqlBulkCopy) | ~11 min |
| PostgreSQL | 0.60 s | 0.58 s (binary COPY) | ~4 min |
| MySQL 8.4 | 1.29 s | 1.32 s (MySqlBulkCopy) | ~15 min |
| SQLite | 0.71 s | 0.71 s (prepared INSERT in one transaction) | 3–11 min |
| MongoDB 8 | 1.24 s | 1.89 s (driver InsertMany) | — |

EF Core is not benchmarked yet; no claims are made about it.

## Repository

| Path | Contents |
|---|---|
| `src/` | `VelQuery.Abstractions` (incl. the engine-neutral `Engine/` kernel), `VelQuery.Core`, `VelQuery.SqlServer`, `VelQuery.PostgreSQL`, `VelQuery.MySql`, `VelQuery.Sqlite`, `VelQuery.MongoDB`, `VelQuery.Generators`, `VelQuery.MongoDB.Generators`, `VelQuery.Analyzers` |
| `tests/` | Unit, generator golden/runtime, analyzer, and SQL Server / PostgreSQL / MySQL / SQLite / MongoDB integration tests |
| `benchmarks/` | BenchmarkDotNet suites plus `--cpu-profile`, `--alloc-profile` and `--bulk-profile` modes |
| `samples/QuickStart/` | Minimal API + data library over SQL Server |
| `docs/` | PRD, ADRs, database support, provider compatibility, diagnostics |

```bash
dotnet build VelQuery.slnx               # warnings are errors
dotnet test --solution VelQuery.slnx     # integration tests need Docker (SQL Server, PostgreSQL, MySQL, MongoDB containers; SQLite needs none)
```

## Documentation

- Requirements: [PRD v1.0](docs/prd/VelQuery_PRD_v1.0.md) and [amendment v1.1 (multi-database)](docs/prd/VelQuery_PRD_v1.1_Amendment.md)
- Decisions: [architecture decision records](docs/architecture/adr/README.md)
- Databases: [support tiers and roadmap](docs/database-support.md), [provider compatibility](docs/provider-compatibility.md)
- Compiler diagnostics: [VQ1xxx reference](docs/diagnostics.md); [schema-checked SQL and the `velquery` tool](docs/schema-snapshots.md)
- Contributing: [CONTRIBUTING.md](CONTRIBUTING.md) and conventions in [AGENTS.md](AGENTS.md)
- Security: [SECURITY.md](SECURITY.md); changes: [CHANGELOG.md](CHANGELOG.md)
- Reports: [status, performance, quality and security](docs/reports/README.md)

## License

Apache-2.0 is proposed for the core (PRD §25); not yet finalized.
