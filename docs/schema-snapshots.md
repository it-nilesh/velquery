# Schema-checked SQL

VelQuery can check your SQL against your real database schema **at build time**. A misspelled table, a column that
was renamed, a nullable column read into a non-nullable property: these become build errors and warnings with the
exact location in your SQL string, instead of exceptions in production.

The build never connects to a database. It reads a **schema snapshot**: a JSON file with table and column names,
types, nullability and defaults, which you export once with the `velquery` tool and commit with your code.

```text
GetProduct.cs(8,22): error VQ1005: Query 'products.by-id': column 'Descripton' in 'dbo.Products' does not exist
                     in the schema snapshot; did you mean 'Description'?
```

## Set up (5 minutes)

1. Install the tool and export a snapshot from a development or staging database (use a read-only account):

   ```bash
   dotnet tool install --global VelQuery.Cli
   export SHOP_DB="Server=localhost;Database=Shop;User Id=readonly;Password=...;TrustServerCertificate=True"
   velquery schema snapshot --provider sqlserver --connection-env SHOP_DB --output Database/shop.vqschema.json
   ```

   Providers: `sqlserver`, `postgresql`, `mysql` (also MariaDB), `sqlite`. Pass the connection string through an
   environment variable (`--connection-env`); it is never written to the file or printed.

2. Put the file in the project that declares your `[VelQuerySql]` contracts. Any `*.vqschema.json` file under the
   project directory is picked up automatically. A snapshot stored elsewhere can be added explicitly:

   ```xml
   <ItemGroup>
     <AdditionalFiles Include="../schema/shop.vqschema.json" />
   </ItemGroup>
   ```

3. Build. Every contract is now checked. Commit the snapshot and review its diff like any other code change.

A project uses one snapshot. Its `dialect` must match the project's SQL dialect (set by the provider package); a
contract with a different `Dialect` is not checked (VQ1011).

## What is checked

| ID | Severity | Finds |
|---|---|---|
| VQ1005 | Error | A table, view or column that does not exist (with a "did you mean" suggestion) |
| VQ1025 | Error | A constructor parameter or `init`/`required` member with no matching result column |
| VQ1028 | Error | The same column name returned twice (for example `u.Id` and `o.Id` in a join) |
| VQ1026 | Warning | A column that can be NULL (also through `LEFT`/`RIGHT`/`FULL JOIN` and `OUTER APPLY`) read into a non-nullable member |
| VQ1027 | Warning | A column type that can never convert to the member type (for example `nvarchar` into `int`) |
| VQ1029 | Warning | An INSERT that leaves out a NOT NULL column without a default |
| VQ1030 | Warning | An INSERT or UPDATE that writes a computed, `GENERATED ALWAYS` or `rowversion` column |
| VQ1011 | Info | SQL outside the checked subset (for example `MERGE`, `FOR JSON`, stored procedures) |
| VQ1031 | Warning | A snapshot file that cannot be used (invalid, wrong format, or more than one per project) |

Covered SQL: `SELECT` with joins, derived tables, CTEs (also recursive), correlated and `IN`/`EXISTS` subqueries,
`UNION`/`INTERSECT`/`EXCEPT`, window functions, `TOP`/`LIMIT`/`OFFSET ... FETCH`, `LATERAL` and `APPLY`; `INSERT`
(column lists, `VALUES`, `SELECT`, `DEFAULT VALUES`, MySQL `SET`, `ON CONFLICT`, `ON DUPLICATE KEY UPDATE`, `OUTPUT`,
`RETURNING`); `UPDATE` and `DELETE` including SQL Server `UPDATE alias ... FROM`, PostgreSQL `UPDATE ... FROM` /
`DELETE ... USING` and MySQL multi-table forms; and batches (`SET NOCOUNT ON; SELECT ...`, `SELECT ... INTO #t; ...`).

Name rules follow each database: PostgreSQL folds unquoted names to lower case and matches quoted names exactly;
MySQL table names are case-sensitive when the server's `lower_case_table_names` is 0 (the snapshot records this);
SQL Server follows the database collation; SQLite ignores case. Unqualified names resolve through the default
schema (`dbo`, `public`, the MySQL database) and then any schema.

## What is deliberately not checked

The checker reports only what it can prove. When it does not understand part of a query, it skips that part rather
than guessing, so a build error always means a real problem:

- Names that come from places the snapshot does not describe: temporary tables (`#t`), table variables,
  table-valued functions, `VALUES` lists, system catalogs (`sys.*`, `pg_catalog`, `information_schema`), and
  three-part cross-database names.
- Expressions without an alias (`SELECT COUNT(*)`): their result name is unknown, so result checks are relaxed.
- Types and nullability of computed expressions (`SUM(x)`, `CASE`, `COALESCE`), and every type check on SQLite
  (its driver converts values itself).
- Stored procedures, `MERGE`, `EXEC`, dynamic SQL and `FOR JSON`/`FOR XML` results (reported as VQ1011).
- Permissions, triggers, constraints other than NOT NULL, and whether the snapshot is still current (see below).

Static checks complement integration tests; they do not replace them (PRD §7.1).

## Keep the snapshot current

Re-run `velquery schema snapshot` after a migration and commit the result. The file is deterministic: the same
schema always produces the same bytes, ordered by schema and table, with no timestamps, so the diff shows exactly
what changed.

In CI, fail the build when the snapshot no longer matches the database you deploy to:

```bash
velquery schema snapshot --provider postgresql --connection-env STAGING_DB --output Database/shop.vqschema.json --check
```

Or classify the drift instead (exit code 1 only for breaking changes; add `--fail-on any` to fail on every change):

```bash
velquery schema diff --snapshot Database/shop.vqschema.json --provider postgresql --connection-env STAGING_DB
```

```text
3 change(s), 2 breaking:
  BREAKING  column removed       public.customers.email
  BREAKING  became nullable      public.orders.total
  ok        table added          public.audit
```

Breaking means code checked against the snapshot can now fail: a removed table, view or column; a changed or shrunk
type; a column that became nullable; or a column that became required for INSERT.

## Opting out

- One diagnostic: `#pragma warning disable VQ1026` around the contract, or `dotnet_diagnostic.VQ1026.severity = none`
  in `.editorconfig`.
- Make warnings errors in strict CI: `dotnet_diagnostic.VQ1026.severity = error`.
- The whole feature for a project: `<EnableVelQuerySchemaSnapshots>false</EnableVelQuerySchemaSnapshots>`.

## Snapshot format

`velquery.schema.v1` (PRD §11.2):

```json
{
  "format": "velquery.schema.v1",
  "dialect": "sqlserver",
  "databaseLogicalName": "Shop",
  "defaultSchema": "dbo",
  "caseSensitiveTables": false,
  "caseSensitiveColumns": false,
  "snapshotSha256": "7c16…",
  "objects": [
    {
      "schema": "dbo",
      "name": "Products",
      "kind": "table",
      "columns": [
        { "name": "Id", "dbType": "int", "nullable": false, "hasDefault": true },
        { "name": "Name", "dbType": "nvarchar", "maxLength": 200, "nullable": false },
        { "name": "Price", "dbType": "decimal", "precision": 18, "scale": 2, "nullable": false }
      ]
    }
  ]
}
```

`nullable` is omitted when the database does not report it (PostgreSQL and SQLite view columns). `hasDefault` means an
INSERT may leave the column out (default, identity, serial, auto-increment, rowid); `generated` means the database
computes it. `snapshotSha256` is the SHA-256 of the file without that line, for review tooling. The file contains
metadata only: never rows, credentials, grants or comments.
