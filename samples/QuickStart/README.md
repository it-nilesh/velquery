# QuickStart sample

A minimal ASP.NET Core API over SQL Server using VelQuery: compile-time-checked generated queries for CRUD, a
streamed export, and a runtime-SQL search with bound parameters, behind an injectable repository.

## Run

```bash
cd samples/QuickStart
docker compose up -d                                   # SQL Server on localhost:1433
dotnet run --project QuickStart.Api --launch-profile http   # http://localhost:5080
```

On startup the API waits for SQL Server, creates the `VelQuerySample` database and runs the embedded scripts in
`QuickStart.Data/Database/Scripts` (table + seed rows). Try the requests in
[QuickStart.Api/QuickStart.Api.http](QuickStart.Api/QuickStart.Api.http), or:

```bash
curl http://localhost:5080/products
curl -X POST http://localhost:5080/products -H 'Content-Type: application/json' \
     -d '{"name":"Ergonomic mouse","description":"Vertical grip","price":59.90,"stock":40}'
```

Stop the database with `docker compose down`. On x64 hosts you can use SQL Server 2022 instead of Azure SQL Edge:
`VELQUERY_SQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest docker compose up -d`.

## Structure

```text
samples/QuickStart/
├── docker-compose.yml                  Local SQL Server
├── QuickStart.Api/                     HTTP layer — depends only on QuickStart.Data
│   ├── Program.cs                      Composition root: AddProductData, problem details, startup DB init
│   ├── Endpoints/ProductEndpoints.cs   Minimal API routes and request validation responses
│   ├── ErrorHandling/                  DataExceptionHandler: data-layer exceptions → HTTP problems
│   ├── Logging/                        Source-generated log messages
│   └── QuickStart.Api.http             Ready-made requests
└── QuickStart.Data/                    Data layer — the only project that knows VelQuery and SQL Server
    ├── Models/                         Product (row), ProductInput (+ validation)
    ├── Exceptions/                     DuplicateProductNameException
    ├── Queries/Products/               One internal [VelQuerySql] contract per file
    ├── Repositories/                   IProductRepository (public) + ProductRepository (internal)
    ├── Database/                       DatabaseInitializer + Scripts/*.sql (embedded, idempotent)
    ├── DependencyInjection/            AddProductData(connectionString)
    └── Logging/                        Source-generated log messages
```

Namespaces follow folders (for example `QuickStart.Data.Repositories`).

## Design

- **Generated queries stay static** (no allocation, AOT-safe, checked at build time) and `internal`, so the rest of the
  app can only reach them through `IProductRepository`, which can be faked in endpoint tests.
- **SQL Server details stay in the data layer.** The repository turns duplicate-key errors (2627/2601) into
  `DuplicateProductNameException`; the API never references SqlClient.
- **No hidden retries.** The startup wait for the container is explicit application code; VelQuery never retries.

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

Errors: validation → 400, duplicate name → 409, database timeout → 504, other database errors → 503.

Try misspelling `@Id` in [Queries/Products/GetProduct.cs](QuickStart.Data/Queries/Products/GetProduct.cs): the build
fails with VQ1001.

The `sa` password in `appsettings.json` and `docker-compose.yml` is for this local container only. Use user secrets or
environment variables (`ConnectionStrings__Products`) for anything else.
