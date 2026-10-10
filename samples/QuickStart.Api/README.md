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

## Endpoints

| Method | Route | VelQuery feature |
|---|---|---|
| GET | `/products?skip=0&take=20` | Generated `Task<IReadOnlyList<Product>>` query |
| GET | `/products/{id}` | Generated `Task<Product?>` (SingleOrDefault) → 404 when missing |
| POST | `/products` | Generated scalar insert (`OUTPUT INSERTED.Id`) with sized/precise parameters |
| PUT | `/products/{id}` | Generated non-query; affected-row count 0 → 404 |
| DELETE | `/products/{id}` | Generated non-query |
| GET | `/products/search?q=` | Runtime SQL with `QueryParameters`, per-call timeout and query ID |
| GET | `/products/export` | Generated `IAsyncEnumerable<Product>` streamed to the response |

## Where to look

- [Data/ProductQueries.cs](Data/ProductQueries.cs): `[VelQuerySql]` contracts. Misspell `@Id` in one of them and the
  build fails with VQ1001.
- [Program.cs](Program.cs): `AddVelQuerySqlServer` registers the data source and a scoped `IVelQuerySession`.
- [VelQueryExceptionHandler.cs](VelQueryExceptionHandler.cs): maps VelQuery error categories to problem responses
  (unique-key violation → 409, timeout → 504, other provider errors → 503).

The `sa` password in `appsettings.json` and `docker-compose.yml` is for this local container only. Use user secrets
or environment variables (`ConnectionStrings__Products`) for anything else.
