# QuickStart.Api

A minimal ASP.NET Core API over SQL Server using VelQuery: generated, compile-time-checked queries for CRUD,
a streamed export, and a runtime-SQL search with bound parameters.

## Run

```bash
cd samples/QuickStart.Api
docker compose up -d          # SQL Server on localhost:1433 (Azure SQL Edge on ARM64; see docker-compose.yml)
dotnet run --launch-profile http
```

On startup the app waits for SQL Server, creates the `VelQuerySample` database and `dbo.Products`, and seeds
three rows. Then try the requests in [QuickStart.Api.http](QuickStart.Api.http), or:

```bash
curl http://localhost:5080/products
curl -X POST http://localhost:5080/products -H 'Content-Type: application/json' \
     -d '{"name":"Ergonomic mouse","description":"Vertical grip","price":59.90,"stock":40}'
```

Stop the database with `docker compose down`.

## Projects

| Project | Contents |
|---|---|
| [QuickStart.Api](.) | Minimal API endpoints, validation responses, and mapping of data errors to HTTP problems. Depends only on `IProductRepository`. |
| [QuickStart.Data](../QuickStart.Data) | `Product`/`ProductInput`, the `[VelQuerySql]` queries (internal), `IProductRepository` + `ProductRepository`, schema setup, and `AddProductData(connectionString)`. |

The generated queries stay static (no allocation, AOT-safe, compile-time checked); the repository wraps them behind
an interface so endpoint code can be unit-tested with a fake. The repository also converts SQL Server duplicate-key
errors into `DuplicateProductNameException`, so the API never references SqlClient.

## Endpoints

| Method | Route | Repository method → VelQuery feature |
|---|---|---|
| GET | `/products?skip=0&take=20` | `ListAsync` → generated `Task<IReadOnlyList<Product>>` |
| GET | `/products/{id}` | `GetAsync` → generated `Task<Product?>` (SingleOrDefault); 404 when missing |
| POST | `/products` | `CreateAsync` → generated insert returning the row via `OUTPUT INSERTED.*` (one round trip) |
| PUT | `/products/{id}` | `UpdateAsync` → generated non-query; 0 rows → 404 |
| DELETE | `/products/{id}` | `DeleteAsync` → generated non-query |
| GET | `/products/search?q=` | `SearchAsync` → runtime SQL with `QueryParameters`, per-call timeout and query ID |
| GET | `/products/export` | `ExportAsync` → generated `IAsyncEnumerable<Product>` streamed to the response |

Errors: duplicate name → 409, validation → 400, database timeout → 504, other database errors → 503.

## Where to look

- [QuickStart.Data/ProductQueries.cs](../QuickStart.Data/ProductQueries.cs): misspell `@Id` in a query and the build
  fails with VQ1001.
- [QuickStart.Data/ProductRepository.cs](../QuickStart.Data/ProductRepository.cs): the only code that calls the
  generated queries.
- [DataExceptionHandler.cs](DataExceptionHandler.cs): HTTP mapping for data-layer exceptions.

The `sa` password in `appsettings.json` and `docker-compose.yml` is for this local container only. Use user secrets
or environment variables (`ConnectionStrings__Products`) for anything else.
