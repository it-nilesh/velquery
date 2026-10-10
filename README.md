# VelQuery

Query-first data access for .NET 10: hand-written SQL with compile-time checks, source-generated typed mapping,
strict runtime semantics and native bulk ingestion. Not an ORM.

> **Status: pre-alpha (preview).** SQL Server and PostgreSQL providers are implemented and pass their integration
> suites locally; CI, Native AOT verification and NuGet packages do not exist yet. Do not use in production.

## Why VelQuery

- **Mistakes fail the build.** A misspelled `@parameter` is error VQ1001; SQL built by string interpolation is VQ1008.
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

A complete minimal API with a repository layer is in [samples/QuickStart](samples/QuickStart/README.md).

## Databases

| Database | Status | Package |
|---|---|---|
| SQL Server / Azure SQL | Preview | `VelQuery.SqlServer` |
| PostgreSQL | Preview | `VelQuery.PostgreSQL` |
| MySQL/MariaDB, SQLite, Oracle, analytics, distributed SQL | Planned (phase 1) | — |
| Document, graph, key-value, wide-column, search, vector | Planned (phases 3–4) | — |

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

EF Core is not benchmarked yet; no claims are made about it.

## Repository

| Path | Contents |
|---|---|
| `src/` | `VelQuery.Abstractions`, `VelQuery.Core`, `VelQuery.SqlServer`, `VelQuery.PostgreSQL`, `VelQuery.Generators`, `VelQuery.Analyzers` |
| `tests/` | Unit, generator golden/runtime, analyzer, SQL Server and PostgreSQL integration tests |
| `benchmarks/` | BenchmarkDotNet suites plus `--cpu-profile`, `--alloc-profile` and `--bulk-profile` modes |
| `samples/QuickStart/` | Minimal API + data library over SQL Server |
| `docs/` | PRD, ADRs, database support, provider compatibility, diagnostics |

```bash
dotnet build VelQuery.slnx               # warnings are errors
dotnet test --solution VelQuery.slnx     # integration tests need Docker (SQL Server and PostgreSQL containers)
```

## Documentation

- Requirements: [PRD v1.0](docs/prd/VelQuery_PRD_v1.0.md) and [amendment v1.1 (multi-database)](docs/prd/VelQuery_PRD_v1.1_Amendment.md)
- Decisions: [architecture decision records](docs/architecture/adr/README.md)
- Databases: [support tiers and roadmap](docs/database-support.md), [provider compatibility](docs/provider-compatibility.md)
- Compiler diagnostics: [VQ1xxx reference](docs/diagnostics.md)
- Contributing and conventions: [AGENTS.md](AGENTS.md)

## License

Apache-2.0 is proposed for the core (PRD §25); not yet finalized.
