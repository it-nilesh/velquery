using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;
using VelQuery.Cli.Schema;
using Xunit;

namespace VelQuery.Cli.Tests;

/// <summary>Values bound to <c>@name</c> placeholders when cases are executed.</summary>
internal static class Values
{
    public static readonly IReadOnlyDictionary<string, object> All = new Dictionary<string, object>
    {
        ["id"] = 1,
        ["p_id"] = 1,
        ["name"] = "Ann",
        ["email"] = "ann@example.com",
        ["status"] = "new",
        ["total"] = 10.5m,
        ["min"] = 0m,
    };

    public const string Rows = """
        public sealed record CustomerRow(int Id, string Name, string? Email);
        public sealed record StrictCustomer(int Id, string Name, string Email);
        public sealed record WrongTypes(int Id, int Name, string? Email);
        public sealed record CustomerTotal(int Id, string Name, decimal Total);
        public sealed record BigOrder(int Id, int CustomerId, decimal Total);
        public sealed record RegionRow(string Code, string Name);
        public sealed record IdName(int Id, string Name);

        """;
}

/// <summary>SQLite: no Docker needed, so this runs everywhere.</summary>
public sealed class SqliteEndToEndTests : IDisposable
{
    private const string Ddl = """
        CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
        CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES customers(id), total NUMERIC NOT NULL,
            status TEXT NOT NULL, shipped_at TEXT, total_with_tax REAL GENERATED ALWAYS AS (total * 1.2) VIRTUAL);
        CREATE VIEW big_orders AS SELECT id, customer_id, total FROM orders WHERE total > 100;
        INSERT INTO customers (name, email) VALUES ('Ann', NULL), ('Bob', 'bob@example.com');
        INSERT INTO orders (customer_id, total, status) VALUES (1, 10, 'new'), (2, 250, 'paid');
        """;

    private readonly string _path = Path.Combine(Path.GetTempPath(), "velquery-cli-" + Guid.NewGuid().ToString("N") + ".db");

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ConnectionString;

    // Valid SQL beyond the basics: none of it may produce a diagnostic, and all of it must run.
    private static readonly QueryCase[] Complex =
    [
        new("cx_scalar_subquery", "SELECT c.id, c.name, (SELECT COUNT(*) FROM orders o WHERE o.customer_id = c.id) AS order_count FROM customers c", "Task<IReadOnlyList<IdName>>", []),
        new("cx_in_subquery", "SELECT id, name FROM customers WHERE id IN (SELECT customer_id FROM orders WHERE total > @min)", "Task<IReadOnlyList<IdName>>", []),
        new("cx_window", "SELECT id, name, ROW_NUMBER() OVER (PARTITION BY name ORDER BY id DESC) AS rn FROM customers", "Task<IReadOnlyList<IdName>>", []),
        new("cx_union", "SELECT id, name FROM customers UNION SELECT id, status FROM orders ORDER BY 1", "Task<IReadOnlyList<IdName>>", []),
        new("cx_case", "SELECT id, CASE WHEN email IS NULL THEN 'none' ELSE email END AS name FROM customers", "Task<IReadOnlyList<IdName>>", []),
        new("cx_group_concat", "SELECT customer_id AS id, group_concat(status, ',') AS name FROM orders GROUP BY customer_id HAVING COUNT(*) > 0", "Task<IReadOnlyList<IdName>>", []),
        new("cx_recursive", "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 5) SELECT x AS id, 'n' AS name FROM n", "Task<IReadOnlyList<IdName>>", []),
        new("cx_functions", "SELECT id, coalesce(email, name) AS name FROM customers WHERE strftime('%Y', created_at) > '2000' AND json_valid('{}') ORDER BY name LIMIT 5 OFFSET 0", "Task<IReadOnlyList<IdName>>", []),
        new("cx_insert_or_ignore", "INSERT OR IGNORE INTO customers (id, name) VALUES (@id, @name)", "Task<int>", [], "NonQuery"),
        new("cx_update_from", "UPDATE orders SET status = c.name FROM customers c WHERE c.id = orders.customer_id", "Task<int>", [], "NonQuery"),
        new("cx_delete_returning", "DELETE FROM orders WHERE id = -1 RETURNING id", "Task<IReadOnlyList<int>>", []),
        new("cx_not_exists", "SELECT id, name FROM customers c WHERE NOT EXISTS (SELECT 1 FROM orders o WHERE o.customer_id = c.id AND o.status IN ('x', 'y'))", "Task<IReadOnlyList<IdName>>", []),
        new("cx_glob_rowid", "SELECT rowid AS id, name FROM customers WHERE name GLOB 'A*' ORDER BY rowid", "Task<IReadOnlyList<IdName>>", []),
        new("cx_natural_join", "SELECT c.id, c.name FROM customers c NATURAL JOIN customers", "Task<IReadOnlyList<IdName>>", []),
        new("cx_temp_table_batch", "CREATE TEMP TABLE tmp AS SELECT id, name FROM customers; SELECT id, name FROM tmp", "Task<IReadOnlyList<IdName>>", []),
    ];

    [Fact]
    public async Task Exported_snapshot_checks_match_what_sqlite_executes()
    {
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqliteCommand(Ddl, connection);
            await command.ExecuteNonQueryAsync();
        }

        var schema = await SchemaReaders.ReadAsync("sqlite", ConnectionString, [], null, TestContext.Current.CancellationToken);
        var customers = Assert.Single(schema.Objects, o => o.Name == "customers");
        Assert.Equal([false, false, true, false], customers.Columns.Select(c => c.Nullable!.Value)); // INTEGER PRIMARY KEY is the rowid
        Assert.True(customers.Columns[0].HasDefault);
        Assert.True(Assert.Single(schema.Objects, o => o.Name == "orders").Columns[^1].Generated);
        Assert.Null(Assert.Single(schema.Objects, o => o.Name == "big_orders").Columns[0].Nullable);

        QueryCase[] cases =
        [
            new("ok_by_id", "SELECT id, name, email FROM customers WHERE id = @id", "Task<CustomerRow?>", []),
            new("ok_join", "SELECT c.id, c.name, o.total FROM customers c JOIN orders o ON o.customer_id = c.id WHERE o.status = @status ORDER BY o.total DESC LIMIT 10", "Task<IReadOnlyList<CustomerTotal>>", []),
            new("ok_view", "SELECT id, customer_id AS CustomerId, total FROM big_orders", "Task<IReadOnlyList<BigOrder>>", []),
            new("ok_cte", "WITH t AS (SELECT customer_id, SUM(total) AS total FROM orders GROUP BY customer_id) SELECT c.id, c.name, t.total FROM customers c JOIN t ON t.customer_id = c.id", "Task<IReadOnlyList<CustomerTotal>>", []),
            new("ok_insert_returning", "INSERT INTO customers (name, email) VALUES (@name, @email) RETURNING id", "Task<int>", [], "Scalar"),
            new("ok_upsert", "INSERT INTO customers (id, name) VALUES (@id, @name) ON CONFLICT (id) DO UPDATE SET name = excluded.name", "Task<int>", [], "NonQuery"),
            new("bad_column", "SELECT id, name, emial FROM customers", "Task<IReadOnlyList<CustomerRow>>", ["VQ1005"]),
            new("bad_table", "SELECT id FROM customer", "Task<IReadOnlyList<int>>", ["VQ1005"]),
            new("bad_insert_required", "INSERT INTO orders (customer_id, total) VALUES (@id, @total)", "Task<int>", ["VQ1029"], "NonQuery"),
            new("bad_generated", "UPDATE orders SET total_with_tax = 1 WHERE id = @id", "Task<int>", ["VQ1030"], "NonQuery"),
            new("warn_nullable", "SELECT id, name, email FROM customers", "Task<IReadOnlyList<StrictCustomer>>", ["VQ1026"]),
            new("warn_outer_join", "SELECT c.id, c.name, o.total FROM customers c LEFT JOIN orders o ON o.customer_id = c.id", "Task<IReadOnlyList<CustomerTotal>>", ["VQ1026"]),
            new("err_duplicate", "SELECT c.id, o.id, c.name, o.total FROM customers c JOIN orders o ON o.customer_id = c.id", "Task<IReadOnlyList<CustomerTotal>>", ["VQ1028"]),
        ];

        cases = [.. cases, .. Complex];
        await SchemaScenario.RunAsync("sqlite", schema, Values.Rows, cases, async () =>
        {
            var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            return connection;
        }, Values.All);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }
}

/// <summary>PostgreSQL in a container (Testcontainers).</summary>
public sealed class PostgreSqlEndToEndTests
{
    private const string Ddl = """
        CREATE TABLE customers (id serial PRIMARY KEY, name text NOT NULL, email text NULL, created_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE orders (id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY, customer_id int NOT NULL REFERENCES customers(id),
            total numeric(10,2) NOT NULL, status varchar(20) NOT NULL, shipped_at timestamp NULL,
            total_with_tax numeric GENERATED ALWAYS AS (total * 1.2) STORED);
        CREATE SCHEMA sales;
        CREATE TABLE sales.regions (code char(2) PRIMARY KEY, name text NOT NULL);
        CREATE TABLE "Audit" ("Id" bigserial PRIMARY KEY, "Note" text);
        CREATE VIEW big_orders AS SELECT id, customer_id, total FROM orders WHERE total > 100;
        CREATE TABLE order_notes (note_id int PRIMARY KEY, customer_id int NOT NULL, note text NOT NULL);
        CREATE FUNCTION get_customer(p_id int) RETURNS TABLE(id int, name text, email text) LANGUAGE sql AS $$ SELECT c.id, c.name, c.email FROM customers c WHERE c.id = p_id $$;
        CREATE PROCEDURE touch_customer(p_id int) LANGUAGE sql AS $$ UPDATE customers SET name = name WHERE id = p_id $$;
        CREATE MATERIALIZED VIEW customer_totals AS SELECT customer_id AS id, 'c' || customer_id AS name, SUM(total) AS total FROM orders GROUP BY customer_id;
        INSERT INTO customers (name, email) VALUES ('Ann', NULL), ('Bob', 'bob@example.com');
        INSERT INTO orders (customer_id, total, status) VALUES (1, 10, 'new'), (2, 250, 'paid');
        """;

    // Valid SQL beyond the basics: no false diagnostics, and all of it must run.
    private static readonly QueryCase[] Complex =
    [
        new("cx_scalar_subquery", "SELECT c.id, c.name, (SELECT COUNT(*) FROM orders o WHERE o.customer_id = c.id) AS order_count FROM customers c", "Task<IReadOnlyList<IdName>>", []),
        new("cx_in_subquery", "SELECT id, name FROM customers WHERE id IN (SELECT customer_id FROM orders WHERE total > @min)", "Task<IReadOnlyList<IdName>>", []),
        new("cx_window", "SELECT id, name, ROW_NUMBER() OVER (PARTITION BY name ORDER BY id DESC) AS rn FROM customers", "Task<IReadOnlyList<IdName>>", []),
        new("cx_union", "SELECT id, name FROM customers UNION SELECT id, status FROM orders ORDER BY 1", "Task<IReadOnlyList<IdName>>", []),
        new("cx_lateral", "SELECT x.id, x.name FROM (SELECT id, name FROM customers WHERE email IS NOT NULL) x JOIN LATERAL (SELECT total FROM orders o WHERE o.customer_id = x.id LIMIT 1) y ON true", "Task<IReadOnlyList<IdName>>", []),
        new("cx_case", "SELECT id, CASE WHEN email IS NULL THEN 'none' ELSE email END AS name FROM customers", "Task<IReadOnlyList<IdName>>", []),
        new("cx_string_agg", "SELECT customer_id AS id, string_agg(status, ',' ORDER BY status) AS name FROM orders GROUP BY customer_id HAVING COUNT(*) > 0", "Task<IReadOnlyList<IdName>>", []),
        new("cx_any_array", "SELECT id, name FROM customers c WHERE NOT EXISTS (SELECT 1 FROM orders o WHERE o.customer_id = c.id AND o.status = ANY(ARRAY['x', 'y']))", "Task<IReadOnlyList<IdName>>", []),
        new("cx_nulls_last", "SELECT id, coalesce(email, name) AS name FROM customers ORDER BY name NULLS LAST LIMIT 5 OFFSET 0", "Task<IReadOnlyList<IdName>>", []),
        new("cx_distinct_on", "SELECT DISTINCT ON (customer_id) customer_id AS id, status AS name FROM orders ORDER BY customer_id, id DESC", "Task<IReadOnlyList<IdName>>", []),
        new("cx_update_returning", "UPDATE customers SET email = lower(email) WHERE id IN (SELECT customer_id FROM orders) RETURNING id, name", "Task<IReadOnlyList<IdName>>", []),
        new("cx_delete_using", "DELETE FROM orders USING customers c WHERE c.id = orders.customer_id AND c.name = @name", "Task<int>", [], "NonQuery"),
        new("cx_casts_extract", "SELECT id, name FROM customers WHERE created_at::date <= current_date OR extract(year FROM created_at) > 2000", "Task<IReadOnlyList<IdName>>", []),
        new("cx_recursive", "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 5) SELECT x AS id, 'n' AS name FROM n", "Task<IReadOnlyList<IdName>>", []),
        new("sp_ok", "touch_customer", "Task<int>", [], "NonQuery", Procedure: ["p_id"]),
        new("sp_missing", "tuch_customer", "Task<int>", ["VQ1005"], "NonQuery", Procedure: ["p_id"]),
        new("sp_wrong_parameter", "public.touch_customer", "Task<int>", ["VQ1005"], "NonQuery", Procedure: ["name"]),
        new("sp_function", "get_customer", "Task<CustomerRow?>", ["VQ1005"], Procedure: ["p_id"]),
        new("cx_materialized_view", "SELECT id, name, total FROM customer_totals", "Task<IReadOnlyList<CustomerTotal>>", []),
        new("cx_using_star", "SELECT * FROM orders JOIN order_notes USING (customer_id)", "Task<IReadOnlyList<OrderNote>>", []),
        new("cx_natural_star", "SELECT * FROM customers NATURAL JOIN customers c2", "Task<IReadOnlyList<CustomerRow>>", []),
        new("cx_temp_table_batch", "CREATE TEMP TABLE tmp AS SELECT id, name FROM customers; SELECT id, name FROM tmp", "Task<IReadOnlyList<IdName>>", []),
        new("cx_overriding_identity", "INSERT INTO orders (id, customer_id, total, status) OVERRIDING SYSTEM VALUE VALUES (100, @id, @total, @status)", "Task<int>", [], "NonQuery"),
        new("cx_right_join", "SELECT o.id, c.name FROM orders o RIGHT JOIN customers c ON c.id = o.customer_id", "Task<IReadOnlyList<IdName>>", ["VQ1026"]),
        new("cx_full_join", "SELECT c.id, c.name FROM customers c FULL JOIN orders o ON o.customer_id = c.id", "Task<IReadOnlyList<IdName>>", ["VQ1026", "VQ1026"]),
    ];

    [Fact]
    public async Task Exported_snapshot_checks_match_what_postgresql_executes()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("shop").Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        var connectionString = container.GetConnectionString();
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(Ddl, connection);
            await command.ExecuteNonQueryAsync();
        }

        var schema = await SchemaReaders.ReadAsync("postgresql", connectionString, [], "shop", TestContext.Current.CancellationToken);
        Assert.Equal("public", schema.DefaultSchema);
        var orders = Assert.Single(schema.Objects, o => o.Name == "orders");
        Assert.True(orders.Columns[0].Generated); // GENERATED ALWAYS AS IDENTITY
        Assert.Equal((10, 2), (orders.Columns[2].Precision, orders.Columns[2].Scale));
        Assert.Null(Assert.Single(schema.Objects, o => o.Name == "big_orders").Columns[0].Nullable);

        QueryCase[] cases =
        [
            new("ok_by_id", "SELECT id, name, email FROM customers WHERE id = @id", "Task<CustomerRow?>", []),
            new("ok_case_folding", "SELECT Id, Name, Email FROM Customers WHERE Name ILIKE @name", "Task<IReadOnlyList<CustomerRow>>", []),
            new("ok_join", "SELECT c.id, c.name, o.total FROM public.customers c JOIN orders o ON o.customer_id = c.id WHERE o.status = @status::varchar AND o.total > @min ORDER BY o.total DESC", "Task<IReadOnlyList<CustomerTotal>>", []),
            new("ok_schema", "SELECT code, name FROM sales.regions", "Task<IReadOnlyList<RegionRow>>", []),
            new("ok_cte", "WITH t AS (SELECT customer_id, SUM(total) AS total FROM orders GROUP BY customer_id) SELECT c.id, c.name, t.total FROM customers c JOIN t ON t.customer_id = c.id", "Task<IReadOnlyList<CustomerTotal>>", []),
            new("ok_exists", "SELECT id, name, email FROM customers c WHERE EXISTS (SELECT 1 FROM orders o WHERE o.customer_id = c.id AND o.shipped_at IS NULL) AND c.created_at > now() - interval '1 day'", "Task<IReadOnlyList<CustomerRow>>", []),
            new("ok_upsert", "INSERT INTO customers (id, name, email) VALUES (@id, @name, @email) ON CONFLICT (id) DO UPDATE SET email = EXCLUDED.email RETURNING id", "Task<int>", [], "Scalar"),
            new("ok_update_from", "UPDATE orders o SET status = @status FROM customers c WHERE c.id = o.customer_id AND c.name = @name", "Task<int>", [], "NonQuery"),
            new("ok_quoted", "SELECT \"Id\", \"Note\" FROM \"Audit\"", "Task<IReadOnlyList<AuditRow>>", []),
            new("bad_quoted_case", "SELECT id, name, email FROM \"Customers\"", "Task<IReadOnlyList<CustomerRow>>", ["VQ1005"]),
            new("bad_column", "SELECT id, name, emial FROM customers", "Task<IReadOnlyList<CustomerRow>>", ["VQ1005"]),
            new("bad_qualified", "SELECT c.id, c.nmae, c.email FROM customers c", "Task<IReadOnlyList<CustomerRow>>", ["VQ1005"]),
            new("bad_schema", "SELECT code, name FROM public.regions", "Task<IReadOnlyList<RegionRow>>", ["VQ1005"]),
            new("bad_identity_always", "INSERT INTO orders (id, customer_id, total, status) VALUES (@id, @id, @total, @status)", "Task<int>", ["VQ1030"], "NonQuery"),
            new("bad_insert_required", "INSERT INTO orders (customer_id, total) VALUES (@id, @total)", "Task<int>", ["VQ1029"], "NonQuery"),
            new("warn_nullable", "SELECT id, name, email FROM customers", "Task<IReadOnlyList<StrictCustomer>>", ["VQ1026"]),
            new("warn_type", "SELECT id, name, email FROM customers", "Task<IReadOnlyList<WrongTypes>>", ["VQ1027"]),
            new("err_duplicate", "SELECT c.id, o.id, c.name, o.total FROM customers c JOIN orders o ON o.customer_id = c.id", "Task<IReadOnlyList<CustomerTotal>>", ["VQ1028"]),
        ];

        cases = [.. cases, .. Complex];
        await SchemaScenario.RunAsync("postgresql", schema, Values.Rows + "public sealed record AuditRow(long Id, string? Note);\npublic sealed record OrderNote(int Customer_Id, int Id, decimal Total, string Status, string Note);\n", cases, async () =>
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            return connection;
        }, Values.All);
    }
}

/// <summary>MySQL in a container (Testcontainers).</summary>
public sealed class MySqlEndToEndTests
{
    private const string Ddl = """
        CREATE TABLE customers (id int AUTO_INCREMENT PRIMARY KEY, name varchar(100) NOT NULL, email varchar(200) NULL,
            created_at datetime NOT NULL DEFAULT CURRENT_TIMESTAMP);
        CREATE TABLE orders (id int AUTO_INCREMENT PRIMARY KEY, customer_id int NOT NULL, total decimal(10,2) NOT NULL,
            status varchar(20) NOT NULL, shipped_at datetime NULL, total_with_tax decimal(12,2) AS (total * 1.2) STORED);
        CREATE VIEW big_orders AS SELECT id, customer_id, total FROM orders WHERE total > 100;
        CREATE PROCEDURE get_customer(IN p_id INT) SELECT id, name, email FROM customers WHERE id = p_id;
        INSERT INTO customers (name, email) VALUES ('Ann', NULL), ('Bob', 'bob@example.com');
        INSERT INTO orders (customer_id, total, status) VALUES (1, 10, 'new'), (2, 250, 'paid');
        """;

    // Valid SQL beyond the basics: no false diagnostics, and all of it must run.
    private static readonly QueryCase[] Complex =
    [
        new("sp_ok", "get_customer", "Task<CustomerRow?>", [], Procedure: ["p_id"]),
        new("sp_missing", "get_customr", "Task<CustomerRow?>", ["VQ1005"], Procedure: ["p_id"]),
        new("sp_wrong_parameter", "shop.get_customer", "Task<CustomerRow?>", ["VQ1005"], Procedure: ["name"]),
        new("cx_scalar_subquery", "SELECT c.id, c.name, (SELECT COUNT(*) FROM orders o WHERE o.customer_id = c.id) AS order_count FROM customers c", "Task<IReadOnlyList<IdName>>", []),
        new("cx_in_subquery", "SELECT id, name FROM customers WHERE id IN (SELECT customer_id FROM orders WHERE total > @min)", "Task<IReadOnlyList<IdName>>", []),
        new("cx_window", "SELECT id, name, ROW_NUMBER() OVER (PARTITION BY name ORDER BY id DESC) AS rn FROM customers", "Task<IReadOnlyList<IdName>>", []),
        new("cx_union", "SELECT id, name FROM customers UNION SELECT id, status FROM orders ORDER BY 1", "Task<IReadOnlyList<IdName>>", []),
        new("cx_case_ifnull", "SELECT id, CASE WHEN email IS NULL THEN 'none' ELSE IFNULL(email, '') END AS name FROM customers", "Task<IReadOnlyList<IdName>>", []),
        new("cx_group_concat", "SELECT customer_id AS id, GROUP_CONCAT(status ORDER BY status SEPARATOR ',') AS name FROM orders GROUP BY customer_id HAVING COUNT(*) > 0", "Task<IReadOnlyList<IdName>>", []),
        new("cx_limit_offset", "SELECT id, name FROM customers FORCE INDEX (PRIMARY) WHERE DATE_FORMAT(created_at, '%Y') > '2000' ORDER BY id LIMIT 0, 5", "Task<IReadOnlyList<IdName>>", []),
        new("cx_replace", "REPLACE INTO customers (id, name, email) VALUES (@id, @name, @email)", "Task<int>", [], "NonQuery"),
        new("cx_insert_ignore", "INSERT IGNORE INTO customers (id, name) VALUES (@id, @name)", "Task<int>", [], "NonQuery"),
        new("cx_delete_join", "DELETE o FROM orders o JOIN customers c ON c.id = o.customer_id WHERE c.email IS NULL", "Task<int>", [], "NonQuery"),
        new("cx_utc_sounds_like", "SELECT id, name FROM customers WHERE created_at < UTC_TIMESTAMP AND name SOUNDS LIKE 'Anne' OR CURDATE() IS NOT NULL", "Task<IReadOnlyList<IdName>>", []),
        new("cx_using", "SELECT c.id, c.name FROM customers c JOIN orders o USING (id)", "Task<IReadOnlyList<IdName>>", []),
        new("cx_temp_table_batch", "CREATE TEMPORARY TABLE tmp AS SELECT id, name FROM customers; SELECT id, name FROM tmp", "Task<IReadOnlyList<IdName>>", []),
        new("cx_json", "SELECT id, name FROM customers WHERE JSON_EXTRACT('{\"a\": 1}', '$.a') = 1", "Task<IReadOnlyList<IdName>>", []),
        new("cx_right_join", "SELECT o.id, c.name FROM orders o RIGHT JOIN customers c ON c.id = o.customer_id", "Task<IReadOnlyList<IdName>>", ["VQ1026"]),
    ];

    [Fact]
    public async Task Exported_snapshot_checks_match_what_mysql_executes()
    {
        await using var container = new MySqlBuilder("mysql:8.4").WithDatabase("shop")
            // The whole solution's suites start their databases in parallel; keep this one small (no performance schema,
            // 32 MB buffer pool) so a 2 GB Docker VM does not kill it (exit 137).
            .WithCommand("--performance-schema=OFF", "--innodb-buffer-pool-size=33554432")
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        var connectionString = container.GetConnectionString();
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new MySqlCommand(Ddl, connection);
            await command.ExecuteNonQueryAsync();
        }

        var schema = await SchemaReaders.ReadAsync("mysql", connectionString, [], null, TestContext.Current.CancellationToken);
        Assert.Equal("shop", schema.DefaultSchema);
        Assert.True(schema.CaseSensitiveTables); // lower_case_table_names = 0 on Linux
        var orders = Assert.Single(schema.Objects, o => o.Name == "orders");
        Assert.True(orders.Columns[0].HasDefault); // AUTO_INCREMENT
        Assert.True(orders.Columns[^1].Generated);

        QueryCase[] cases =
        [
            new("ok_by_id", "SELECT id, name, email FROM customers WHERE id = @id", "Task<CustomerRow?>", []),
            new("ok_join", "SELECT c.id, c.name, o.total FROM `customers` c JOIN shop.orders o ON o.customer_id = c.id WHERE o.status = @status ORDER BY o.total DESC LIMIT 10", "Task<IReadOnlyList<CustomerTotal>>", []),
            new("ok_view", "SELECT id, customer_id AS CustomerId, total FROM big_orders", "Task<IReadOnlyList<BigOrder>>", []),
            new("ok_interval", "SELECT id, name, email FROM customers WHERE created_at > NOW() - INTERVAL 1 DAY", "Task<IReadOnlyList<CustomerRow>>", []),
            new("ok_upsert", "INSERT INTO customers (id, name, email) VALUES (@id, @name, @email) ON DUPLICATE KEY UPDATE email = VALUES(email)", "Task<int>", [], "NonQuery"),
            new("ok_multi_table_update", "UPDATE orders o JOIN customers c ON c.id = o.customer_id SET o.status = @status WHERE c.name = @name", "Task<int>", [], "NonQuery"),
            new("ok_insert_set", "INSERT INTO customers SET name = @name, email = @email", "Task<int>", [], "NonQuery"),
            new("bad_table_case", "SELECT id, name, email FROM Customers", "Task<IReadOnlyList<CustomerRow>>", ["VQ1005"]),
            new("bad_column", "SELECT id, name, emial FROM customers", "Task<IReadOnlyList<CustomerRow>>", ["VQ1005"]),
            new("bad_insert_required", "INSERT INTO orders (customer_id, total) VALUES (@id, @total)", "Task<int>", ["VQ1029"], "NonQuery"),
            new("bad_generated", "UPDATE orders SET total_with_tax = 1 WHERE id = @id", "Task<int>", ["VQ1030"], "NonQuery"),
            new("warn_nullable", "SELECT id, name, email FROM customers", "Task<IReadOnlyList<StrictCustomer>>", ["VQ1026"]),
            new("warn_type", "SELECT id, name, email FROM customers", "Task<IReadOnlyList<WrongTypes>>", ["VQ1027"]),
        ];

        cases = [.. cases, .. Complex];
        await SchemaScenario.RunAsync("mysql", schema, Values.Rows, cases, async () =>
        {
            var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            return connection;
        }, Values.All);
    }
}

/// <summary>SQL Server in a container (Azure SQL Edge on ARM64, as in the provider integration tests).</summary>
public sealed class SqlServerEndToEndTests
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE dbo.Customers (Id int IDENTITY PRIMARY KEY, Name nvarchar(100) NOT NULL, Email nvarchar(200) NULL,
            CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(), Version rowversion, Location geography NULL, Node hierarchyid NULL);
        CREATE TABLE dbo.Orders (Id int IDENTITY PRIMARY KEY, CustomerId int NOT NULL REFERENCES dbo.Customers(Id),
            Total decimal(10,2) NOT NULL, Status nvarchar(20) NOT NULL, ShippedAt datetime2 NULL, TotalWithTax AS (Total * 1.2));
        INSERT INTO dbo.Customers (Name, Email) VALUES (N'Ann', NULL), (N'Bob', N'bob@example.com');
        INSERT INTO dbo.Orders (CustomerId, Total, Status) VALUES (1, 10, N'new'), (2, 250, N'paid');
        """,
        "CREATE SCHEMA sales",
        "CREATE TABLE sales.Regions (Code char(2) NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL)",
        "CREATE VIEW dbo.BigOrders AS SELECT Id, CustomerId, Total FROM dbo.Orders WHERE Total > 100",
        "CREATE PROCEDURE dbo.GetCustomer @id int AS SELECT Id, Name, Email FROM dbo.Customers WHERE Id = @id",
    ];

    // Valid T-SQL beyond the basics: no false diagnostics, and all of it must run.
    private static readonly QueryCase[] Complex =
    [
        new("cx_scalar_subquery", "SELECT c.Id, c.Name, (SELECT COUNT(*) FROM Orders o WHERE o.CustomerId = c.Id) AS OrderCount FROM Customers c", "Task<IReadOnlyList<IdName>>", []),
        new("cx_in_subquery", "SELECT Id, Name FROM Customers WHERE Id IN (SELECT CustomerId FROM Orders WHERE Total > @min)", "Task<IReadOnlyList<IdName>>", []),
        new("cx_window", "SELECT Id, Name, ROW_NUMBER() OVER (PARTITION BY Name ORDER BY Id DESC) AS Rn FROM Customers", "Task<IReadOnlyList<IdName>>", []),
        new("cx_union", "SELECT Id, Name FROM Customers UNION SELECT Id, Status FROM Orders ORDER BY 1", "Task<IReadOnlyList<IdName>>", []),
        new("cx_cross_apply", "SELECT c.Id, c.Name FROM Customers c CROSS APPLY (SELECT TOP (1) o.Total FROM Orders o WHERE o.CustomerId = c.Id ORDER BY o.Total DESC) t", "Task<IReadOnlyList<IdName>>", []),
        new("cx_outer_apply", "SELECT c.Id, t.Status AS Name FROM Customers c OUTER APPLY (SELECT TOP (1) o.Status FROM Orders o WHERE o.CustomerId = c.Id) t", "Task<IReadOnlyList<IdName>>", ["VQ1026"]),
        new("cx_case_iif", "SELECT Id, CASE WHEN Email IS NULL THEN N'none' ELSE IIF(Email = N'', N'empty', ISNULL(Email, N'')) END AS Name FROM Customers", "Task<IReadOnlyList<IdName>>", []),
        new("cx_string_agg", "SELECT CustomerId AS Id, STRING_AGG(Status, N',') WITHIN GROUP (ORDER BY Status) AS Name FROM Orders GROUP BY CustomerId HAVING COUNT(*) > 0", "Task<IReadOnlyList<IdName>>", []),
        new("cx_offset_fetch", "SELECT Id, Name FROM Customers WITH (NOLOCK) WHERE Id BETWEEN 1 AND 10 ORDER BY Id OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY OPTION (RECOMPILE)", "Task<IReadOnlyList<IdName>>", []),
        new("cx_like_escape", "SELECT Id, Name FROM Customers WHERE Name LIKE N'A%' ESCAPE N'\\' AND YEAR(CreatedAt) > 2000", "Task<IReadOnlyList<IdName>>", []),
        new("cx_update_top", "UPDATE TOP (1) Customers SET Name = Name WHERE Id = @id", "Task<int>", [], "NonQuery"),
        new("cx_delete_output", "DELETE FROM Orders OUTPUT DELETED.Id WHERE Id = -1", "Task<IReadOnlyList<int>>", []),
        new("cx_temp_table_batch", "SELECT Id INTO #t FROM Customers; SELECT t.Id, c.Name FROM #t t JOIN Customers c ON c.Id = t.Id", "Task<IReadOnlyList<IdName>>", []),
        new("cx_declare_batch", "DECLARE @since datetime2 = DATEADD(day, -1, SYSUTCDATETIME()); SELECT Id, Name FROM Customers WHERE CreatedAt >= @since OR Id = @id", "Task<IReadOnlyList<IdName>>", []),
        new("sp_ok", "dbo.GetCustomer", "Task<CustomerRow?>", [], Procedure: ["id"]),
        new("sp_missing", "dbo.GetCustomr", "Task<CustomerRow?>", ["VQ1005"], Procedure: ["id"]),
        new("sp_wrong_parameter", "dbo.GetCustomer", "Task<CustomerRow?>", ["VQ1005"], Procedure: ["name"]),
        new("cx_select_into_table", "SELECT Id, Name INTO Staging FROM Customers; SELECT s.Id, s.Name FROM Staging s", "Task<IReadOnlyList<IdName>>", []),
        new("cx_set_batch", "SET NOCOUNT ON; SELECT Id, Name FROM Customers WHERE Id >= @id", "Task<IReadOnlyList<IdName>>", []),
        new("cx_merge", "MERGE Customers AS t USING (SELECT 1 AS Id) AS s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET t.Name = t.Name;", "Task<int>", ["VQ1011"], "NonQuery"),
        new("cx_right_join", "SELECT o.Id, c.Name FROM Orders o RIGHT JOIN Customers c ON c.Id = o.CustomerId", "Task<IReadOnlyList<IdName>>", ["VQ1026"]),
    ];

    [Fact]
    public async Task Exported_snapshot_checks_match_what_sql_server_executes()
    {
        var image = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "mcr.microsoft.com/azure-sql-edge:latest" : "mcr.microsoft.com/mssql/server:2022-latest";
        await using var container = new MsSqlBuilder(image)
            .WithPassword("VelQuery_Cli#2026")
            .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "1024") // Shares the Docker VM with the other suites' databases.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("SQL Server is now ready for client connections"))
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        var master = new SqlConnectionStringBuilder(container.GetConnectionString()) { TrustServerCertificate = true, InitialCatalog = "master" };
        await RetryAsync(async () =>
        {
            await using var connection = new SqlConnection(master.ConnectionString);
            await connection.OpenAsync();
            await using var create = new SqlCommand("CREATE DATABASE Shop", connection);
            await create.ExecuteNonQueryAsync();
        });

        var connectionString = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = "Shop" }.ConnectionString;
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            foreach (var batch in Ddl)
            {
                await using var command = new SqlCommand(batch, connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        var schema = await SchemaReaders.ReadAsync("sqlserver", connectionString, [], null, TestContext.Current.CancellationToken);
        Assert.Equal("dbo", schema.DefaultSchema);
        var customers = Assert.Single(schema.Objects, o => o.Name == "Customers");
        Assert.Equal(100, customers.Columns[1].MaxLength);
        Assert.True(customers.Columns[^3].Generated); // rowversion
        Assert.Equal(["geography", "hierarchyid"], customers.Columns.TakeLast(2).Select(c => c.DbType)); // CLR types
        Assert.True(Assert.Single(schema.Objects, o => o.Name == "Orders").Columns[^1].Generated); // computed

        QueryCase[] cases =
        [
            new("ok_by_id", "SELECT Id, Name, Email FROM Customers WHERE Id = @id", "Task<CustomerRow?>", []),
            new("ok_join", "SELECT TOP (10) c.Id, c.Name, o.Total FROM dbo.Customers c WITH (NOLOCK) JOIN dbo.Orders AS o ON o.CustomerId = c.Id WHERE o.Status = @status ORDER BY o.Total DESC", "Task<IReadOnlyList<CustomerTotal>>", []),
            new("ok_view", "SELECT Id, CustomerId, Total FROM BigOrders", "Task<IReadOnlyList<BigOrder>>", []),
            new("ok_schema", "SELECT Code, Name FROM sales.Regions", "Task<IReadOnlyList<RegionRow>>", []),
            new("ok_cte", "WITH t AS (SELECT CustomerId, SUM(Total) AS Total FROM Orders GROUP BY CustomerId) SELECT c.Id, c.Name, t.Total FROM Customers c JOIN t ON t.CustomerId = c.Id", "Task<IReadOnlyList<CustomerTotal>>", []),
            new("ok_functions", "SELECT Id, Name, Email FROM Customers WHERE CreatedAt > DATEADD(day, -7, SYSUTCDATETIME()) AND CONVERT(nvarchar(10), Id) <> N'0' AND CAST(Id AS bigint) > 0", "Task<IReadOnlyList<CustomerRow>>", []),
            new("ok_insert_output", "INSERT INTO Customers (Name, Email) OUTPUT INSERTED.Id VALUES (@name, @email)", "Task<int>", [], "Scalar"),
            new("ok_update_alias", "UPDATE o SET Status = @status FROM Orders o JOIN Customers c ON c.Id = o.CustomerId WHERE c.Name = @name", "Task<int>", [], "NonQuery"),
            new("ok_delete_alias", "DELETE o FROM Orders o JOIN Customers c ON c.Id = o.CustomerId WHERE c.Email IS NULL", "Task<int>", [], "NonQuery"),
            new("bad_column", "SELECT Id, Name, Emial FROM Customers", "Task<IReadOnlyList<CustomerRow>>", ["VQ1005"]),
            new("bad_table", "SELECT Id FROM Customer", "Task<IReadOnlyList<int>>", ["VQ1005"]),
            new("bad_schema", "SELECT Code, Name FROM dbo.Regions", "Task<IReadOnlyList<RegionRow>>", ["VQ1005"]),
            new("bad_insert_required", "INSERT INTO Orders (CustomerId, Total) VALUES (@id, @total)", "Task<int>", ["VQ1029"], "NonQuery"),
            new("bad_computed", "UPDATE Orders SET TotalWithTax = 1 WHERE Id = @id", "Task<int>", ["VQ1030"], "NonQuery"),
            new("warn_nullable", "SELECT Id, Name, Email FROM Customers", "Task<IReadOnlyList<StrictCustomer>>", ["VQ1026"]),
            new("warn_outer_join", "SELECT c.Id, c.Name, o.Total FROM Customers c LEFT JOIN Orders o ON o.CustomerId = c.Id", "Task<IReadOnlyList<CustomerTotal>>", ["VQ1026"]),
            new("warn_type", "SELECT Id, Name, Email FROM Customers", "Task<IReadOnlyList<WrongTypes>>", ["VQ1027"]),
            new("err_duplicate", "SELECT c.Id, o.Id, c.Name, o.Total FROM Customers c JOIN Orders o ON o.CustomerId = c.Id", "Task<IReadOnlyList<CustomerTotal>>", ["VQ1028"]),
        ];

        cases = [.. cases, .. Complex];
        await SchemaScenario.RunAsync("sqlserver", schema, Values.Rows, cases, async () =>
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            return connection;
        }, Values.All);
    }

    /// <summary>Azure SQL Edge logs "ready" slightly before it accepts logins.</summary>
    private static async Task RetryAsync(Func<Task> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (SqlException) when (attempt < 30)
            {
                await Task.Delay(1000);
            }
        }
    }
}
