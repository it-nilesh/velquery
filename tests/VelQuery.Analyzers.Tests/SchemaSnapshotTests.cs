using System.Linq;
using Xunit;

namespace VelQuery.Analyzers.Tests;

/// <summary>
/// Build-time checks against an offline schema snapshot (ADR-0006, VQ-FR-036, VQ-FR-037): each test marks the exact
/// span of every expected diagnostic, so an unexpected (false-positive) diagnostic fails the test too.
/// </summary>
public sealed class SchemaSnapshotTests
{
    private const string SqlServerSnapshot = """
        {
          "format": "velquery.schema.v1",
          "dialect": "sqlserver",
          "defaultSchema": "dbo",
          "objects": [
            { "schema": "dbo", "name": "Users", "kind": "table", "columns": [
              { "name": "Id", "dbType": "bigint", "nullable": false, "hasDefault": true },
              { "name": "Name", "dbType": "nvarchar", "nullable": false },
              { "name": "Email", "dbType": "nvarchar", "nullable": true },
              { "name": "CreatedAt", "dbType": "datetime2", "nullable": false, "hasDefault": true },
              { "name": "RowVer", "dbType": "rowversion", "nullable": false, "generated": true }
            ] },
            { "schema": "dbo", "name": "Orders", "kind": "table", "columns": [
              { "name": "Id", "dbType": "int", "nullable": false, "hasDefault": true },
              { "name": "UserId", "dbType": "bigint", "nullable": false },
              { "name": "Total", "dbType": "decimal", "nullable": false },
              { "name": "Status", "dbType": "nvarchar", "nullable": false },
              { "name": "ShippedAt", "dbType": "datetime2", "nullable": true }
            ] },
            { "schema": "sales", "name": "Products", "kind": "table", "columns": [
              { "name": "Id", "dbType": "int", "nullable": false, "hasDefault": true },
              { "name": "Name", "dbType": "nvarchar", "nullable": false },
              { "name": "Price", "dbType": "decimal", "nullable": false }
            ] },
            { "schema": "dbo", "name": "ActiveUsers", "kind": "view", "columns": [
              { "name": "Id", "dbType": "bigint", "nullable": false },
              { "name": "Name", "dbType": "nvarchar" }
            ] }
          ]
        }
        """;

    private const string PostgresSnapshot = """
        {
          "format": "velquery.schema.v1",
          "dialect": "postgresql",
          "defaultSchema": "public",
          "caseSensitiveTables": true,
          "caseSensitiveColumns": true,
          "objects": [
            { "schema": "public", "name": "products", "kind": "table", "columns": [
              { "name": "id", "dbType": "integer", "nullable": false, "hasDefault": true },
              { "name": "name", "dbType": "text", "nullable": false },
              { "name": "price", "dbType": "numeric", "nullable": false },
              { "name": "created_at", "dbType": "timestamp with time zone", "nullable": false, "hasDefault": true }
            ] },
            { "schema": "public", "name": "stock", "kind": "table", "columns": [
              { "name": "id", "dbType": "integer", "nullable": false },
              { "name": "qty", "dbType": "integer", "nullable": false }
            ] },
            { "schema": "public", "name": "Audit", "kind": "table", "columns": [
              { "name": "Id", "dbType": "bigint", "nullable": false, "generated": true },
              { "name": "Note", "dbType": "text", "nullable": true }
            ] }
          ]
        }
        """;

    private const string Rows = """
        public sealed record UserRow(long Id, string Name, string? Email);
        public sealed record OrderRow(int Id, decimal Total, string Status);
        public sealed record UserOrder(long Id, string Name, decimal Total);
        public sealed record NameRow(long Id, string? Name);
        public sealed record OrderUser(int Id, string Name);
        public sealed record Product(int Id, string Name, decimal Price);
        public sealed record AuditRow(long Id, string? Note);
        public sealed record Stocked(int Id, string Name, decimal Price, int Qty);

        """;

    private static void SqlServer(string markup) => DiagnosticVerifier.Verify(Rows + markup, null, ("/schema/shop.vqschema.json", SqlServerSnapshot));

    private static void Postgres(string markup) => DiagnosticVerifier.Verify(Rows + markup, "postgresql", ("/schema/shop.vqschema.json", PostgresSnapshot));

    private const string Raw = "\"\"\"";

    private static string Contract(string sql, string returnType = "Task<IReadOnlyList<UserRow>>", string parameters = "", string name = "Q", string? cardinality = null)
        => $$"""
            [VelQuerySql("q.{{name}}", {{sql}}{{(cardinality is null ? "" : ", Cardinality = QueryCardinality." + cardinality)}})]
            public static partial class {{name}}
            {
                public static partial {{returnType}} ExecuteAsync(IVelQuerySession session{{parameters}});
            }

            """;

    // ---------------------------------------------------------------- no false positives

    [Fact]
    public void Matching_queries_report_nothing()
        => SqlServer(
            Contract("\"SELECT Id, Name, Email FROM dbo.Users WHERE Id = @id\"", "Task<UserRow?>", ", long id", "A")
            + Contract("\"SELECT u.Id, u.Name, o.Total FROM Users u JOIN dbo.Orders AS o ON o.UserId = u.Id WHERE o.Status = @s ORDER BY o.Total DESC\"", "Task<IReadOnlyList<UserOrder>>", ", string s", "B")
            + Contract("\"SELECT * FROM [dbo].[Users] WITH (NOLOCK)\"", name: "C")
            + Contract("\"SELECT COUNT(*) FROM Users WHERE CreatedAt > DATEADD(day, -7, SYSUTCDATETIME())\"", "Task<long>", name: "D")
            + Contract("\"SELECT TOP (10) Id, Name, COALESCE(Email, N'none') AS Email, CASE WHEN Id > 5 THEN 1 ELSE 0 END AS Big FROM Users ORDER BY Big, Name\"", name: "E")
            + Contract("\"SELECT Id, Name, Email FROM Users u WHERE EXISTS (SELECT 1 FROM Orders o WHERE o.UserId = u.Id AND Total > 10)\"", name: "F")
            + Contract("\"SELECT d.Id, d.Name, d.Email FROM (SELECT Id, Name, Email FROM Users) AS d\"", name: "G")
            + Contract("\"WITH recent AS (SELECT UserId, SUM(Total) AS Total FROM Orders GROUP BY UserId) SELECT u.Id, u.Name, r.Total FROM Users u JOIN recent r ON r.UserId = u.Id\"", "Task<IReadOnlyList<UserOrder>>", name: "H")
            + Contract("\"SELECT CAST(Id AS int) AS Id, Total, Status FROM Orders UNION ALL SELECT Id, Total, Status FROM Orders\"", "Task<IReadOnlyList<OrderRow>>", name: "I")
            + Contract("\"SELECT Id, Name, Email FROM Users u CROSS APPLY OPENJSON(u.Name) j\"", name: "J")
            + Contract("\"SELECT Id, Name FROM ActiveUsers\"", "Task<IReadOnlyList<NameRow>>", name: "K")
            + Contract("\"SELECT UserId AS Id, STRING_AGG(Status, N',') WITHIN GROUP (ORDER BY Status) AS Name FROM Orders GROUP BY UserId\"", "Task<IReadOnlyList<NameRow>>", name: "L")
            + Contract("\"SELECT Id INTO #t FROM Users; SELECT u.Id, u.Name, u.Email FROM #t t JOIN Users u ON u.Id = t.Id\"", name: "M"));

    [Fact]
    public void Writes_that_match_the_schema_report_nothing()
        => SqlServer(
            Contract("\"INSERT INTO Users (Name, Email) OUTPUT INSERTED.Id VALUES (@name, @email)\"", "Task<long>", ", string name, string? email", "A")
            + Contract("\"UPDATE Users SET Name = @name, Email = NULL WHERE Id = @id\"", "Task<int>", ", string name, long id", "B", cardinality: "NonQuery")
            + Contract("\"UPDATE o SET Status = N'shipped' FROM Orders o JOIN Users u ON u.Id = o.UserId WHERE u.Name = @name\"", "Task<int>", ", string name", "C", cardinality: "NonQuery")
            + Contract("\"DELETE FROM Orders WHERE ShippedAt IS NULL AND UserId = @id\"", "Task<int>", ", long id", "D", cardinality: "NonQuery")
            + Contract("\"DELETE o FROM Orders o JOIN Users u ON u.Id = o.UserId WHERE u.Email IS NULL\"", "Task<int>", name: "E", cardinality: "NonQuery")
            + Contract("\"INSERT INTO #staging (Id) SELECT Id FROM Users\"", "Task<int>", name: "F", cardinality: "NonQuery"));

    // ---------------------------------------------------------------- VQ1005 unknown tables and columns

    [Fact]
    public void VQ1005_points_at_an_unknown_table_and_suggests_the_closest_name()
    {
        SqlServer(Contract("\"SELECT Id, Name, Email FROM {|VQ1005:dbo.Usres|}\""));
        var messages = DiagnosticVerifier.Messages(Rows + Contract("\"SELECT Id, Name, Email FROM dbo.Usres\""), null, ("/s.vqschema.json", SqlServerSnapshot));
        Assert.Contains(messages, m => m.Contains("table or view 'dbo.Usres'") && m.Contains("did you mean 'Users'?"));
    }

    [Fact]
    public void VQ1005_checks_schema_qualified_names()
        => SqlServer(Contract("\"SELECT Id, Name, Email FROM {|VQ1005:dbo.Products|}\"") + Contract("\"SELECT Id, Name, Price FROM sales.Products\"", "Task<IReadOnlyList<Product>>", name: "B"));

    [Fact]
    public void VQ1005_points_at_unknown_columns_in_every_clause()
        => SqlServer(Contract(
            "\"SELECT u.Id, u.{|VQ1005:Nmae|} AS Name, Email FROM Users u JOIN Orders o ON o.{|VQ1005:UsrId|} = u.Id WHERE {|VQ1005:Stat|} = @s GROUP BY u.Id ORDER BY {|VQ1005:Totl|}\"",
            parameters: ", string s"));

    [Fact]
    public void VQ1005_in_insert_update_and_delete()
        => SqlServer(
            Contract("\"INSERT INTO Users (Name, {|VQ1005:Mail|}) VALUES (@name, @mail)\"", "Task<int>", ", string name, string mail", "A", cardinality: "NonQuery")
            + Contract("\"UPDATE Users SET {|VQ1005:Nam|} = @name WHERE Id = @id\"", "Task<int>", ", string name, long id", "B", cardinality: "NonQuery")
            + Contract("\"DELETE FROM {|VQ1005:Ordrs|} WHERE Id = @id\"", "Task<int>", ", long id", "C", cardinality: "NonQuery"));

    [Fact]
    public void VQ1005_resolves_columns_through_derived_tables_and_ctes()
        => SqlServer(
            Contract("\"SELECT d.Id, d.Name, d.{|VQ1005:Email|} FROM (SELECT Id, Name FROM Users) d\"", name: "A")
            + Contract("\"WITH t AS (SELECT UserId AS Id FROM Orders) SELECT t.Id, u.Name, u.Email FROM t JOIN Users u ON u.Id = t.{|VQ1005:UserId|}\"", name: "B"));

    [Fact]
    public void Raw_multi_line_literals_point_at_the_name()
    {
        SqlServer(Contract(Raw + "\n        SELECT Id, Name, Email\n        FROM Users\n        WHERE {|VQ1005:Emial|} = @e\n        " + Raw, parameters: ", string e"));
    }

    // ---------------------------------------------------------------- result mapping

    [Fact]
    public void VQ1025_required_member_without_a_column()
        => SqlServer(Contract("{|VQ1025:\"SELECT Id, Name FROM Users\"|}"));

    [Fact]
    public void VQ1025_with_select_star_expands_the_table()
        => SqlServer(Contract("{|VQ1025:\"SELECT * FROM Orders\"|}", "Task<IReadOnlyList<OrderUser>>"));

    [Fact]
    public void VQ1026_nullable_column_into_non_nullable_member_including_outer_joins()
        => SqlServer(
            "public sealed record Strict(long Id, string Name, string Email);\n"
            + Contract("{|VQ1026:\"SELECT Id, Name, Email FROM Users\"|}", "Task<IReadOnlyList<Strict>>", name: "A")
            + Contract("{|VQ1026:\"SELECT u.Id, u.Name, o.Total FROM Users u LEFT JOIN Orders o ON o.UserId = u.Id\"|}", "Task<IReadOnlyList<UserOrder>>", name: "B")
            + Contract("{|VQ1026:\"SELECT ShippedAt FROM Orders WHERE Id = 1\"|}", "Task<DateTime>", name: "C", cardinality: "Scalar"));

    [Fact]
    public void VQ1027_incompatible_type()
        => SqlServer(
            "public sealed record Bad(long Id, int Name, string? Email);\n"
            + Contract("{|VQ1027:\"SELECT Id, Name, Email FROM Users\"|}", "Task<IReadOnlyList<Bad>>"));

    [Fact]
    public void VQ1028_duplicate_column_from_a_join()
        => SqlServer(Contract("{|VQ1028:\"SELECT u.Id, o.Id, u.Name, o.Total FROM Users u JOIN Orders o ON o.UserId = u.Id\"|}", "Task<IReadOnlyList<UserOrder>>"));

    // ---------------------------------------------------------------- writes

    [Fact]
    public void VQ1029_insert_misses_a_required_column()
        => SqlServer(Contract("\"INSERT INTO {|VQ1029:Orders|} (UserId, Total) VALUES (@u, @t)\"", "Task<int>", ", long u, decimal t", cardinality: "NonQuery"));

    [Fact]
    public void VQ1030_writes_a_generated_column()
        => SqlServer(Contract("\"UPDATE Users SET {|VQ1030:RowVer|} = @v WHERE Id = 1\"", "Task<int>", ", byte[] v", cardinality: "NonQuery"));

    // ---------------------------------------------------------------- PostgreSQL rules

    [Fact]
    public void Postgres_folds_unquoted_names_and_respects_quoted_ones()
        => Postgres(
            Contract("\"SELECT Id, Name, Price FROM Products WHERE Price > @min::numeric\"", "Task<IReadOnlyList<Product>>", ", decimal min", "A")
            + Contract(Raw + "SELECT id, name, price FROM {|VQ1005:\"Products\"|} " + Raw, "Task<IReadOnlyList<Product>>", name: "B")
            + Contract(Raw + "SELECT \"Id\", \"Note\" FROM \"Audit\" " + Raw, "Task<IReadOnlyList<AuditRow>>", name: "C")
            + Contract("\"INSERT INTO products (name, price) VALUES (@n, @p) ON CONFLICT (name) DO UPDATE SET price = EXCLUDED.price RETURNING id\"", "Task<int>", ", string n, decimal p", "D", "Scalar")
            + Contract(Raw + "INSERT INTO \"Audit\" ({|VQ1030:\"Id\"|}, \"Note\") VALUES (1, @n) " + Raw, "Task<int>", ", string n", "E", "NonQuery")
            + Contract(Raw + "INSERT INTO \"Audit\" (\"Id\", \"Note\") OVERRIDING SYSTEM VALUE VALUES (1, @n) " + Raw, "Task<int>", ", string n", "G", "NonQuery")
            + Contract("\"SELECT id, name, price FROM products WHERE created_at > now() - interval '1 day' AND name ILIKE @q\"", "Task<IReadOnlyList<Product>>", ", string q", "F"));

    // ---------------------------------------------------------------- code review regressions

    [Fact]
    public void Using_and_natural_joins_return_shared_columns_once()
        => Postgres(
            Contract("\"SELECT * FROM products JOIN stock USING (id)\"", "Task<IReadOnlyList<Stocked>>", name: "A")
            + Contract("\"SELECT * FROM products NATURAL JOIN stock\"", "Task<IReadOnlyList<Stocked>>", name: "B")
            + Contract("\"SELECT * FROM products LEFT JOIN stock USING (id)\"", "Task<IReadOnlyList<Stocked>>".Replace("Stocked", "Product"), name: "C"));

    [Fact]
    public void Tables_created_in_the_batch_are_not_reported()
        => SqlServer(
            Contract("\"SELECT Id INTO Staging FROM Users; SELECT s.Id, u.Name, u.Email FROM Staging s JOIN Users u ON u.Id = s.Id\"", name: "A")
            + Contract("\"CREATE TABLE #x (Id bigint); CREATE TABLE dbo.Scratch (Id bigint); INSERT INTO Scratch (Id) SELECT Id FROM Users; SELECT Id FROM dbo.Scratch\"", "Task<IReadOnlyList<long>>", name: "B"))
        ;

    [Fact]
    public void Postgres_temp_tables_created_in_the_batch_are_not_reported()
        => Postgres(
            Contract("\"CREATE TEMP TABLE tmp AS SELECT id, name, price FROM products; SELECT id, name, price FROM tmp\"", "Task<IReadOnlyList<Product>>", name: "A")
            // The query inside CREATE ... AS is still checked.
            + Contract("\"CREATE TEMP TABLE tmp2 AS SELECT id, {|VQ1005:nmae|} FROM products; SELECT id, name, price FROM tmp2\"", "Task<IReadOnlyList<Product>>", name: "B"));

    [Fact]
    public void Unknown_keywords_and_functions_are_not_reported_as_columns()
        => SqlServer(
            Contract("\"SELECT Id, Name, Email FROM Users WHERE CreatedAt < UTC_TIMESTAMP AND Name GLOB 'a*' AND SOME_NILADIC_THING > 0\"", name: "A")
            + Contract("\"SELECT Id, Name, Email FROM Users WHERE Name SOUNDS LIKE 'x' ORDER BY ROWID\"", name: "B")
            // Still reported: misspellings of real columns, quoted names, qualified names, written columns.
            + Contract("\"SELECT Id, Name, Email FROM Users u WHERE {|VQ1005:Emial|} = '' AND {|VQ1005:[Nope]|} = 1 AND u.{|VQ1005:Nope2|} = 1\"", name: "C")
            + Contract("\"UPDATE Users SET {|VQ1005:Whatever|} = 1\"", "Task<int>", name: "D", cardinality: "NonQuery"));

    [Fact]
    public void Postgres_system_columns_are_valid()
        => Postgres(Contract("\"SELECT p.id, p.name, p.price FROM products p WHERE p.ctid IS NOT NULL AND xmin IS NOT NULL\"", "Task<IReadOnlyList<Product>>"));

    [Fact]
    public void The_same_snapshot_listed_twice_is_one_snapshot()
    {
        var messages = DiagnosticVerifier.Messages(Rows + Contract("\"SELECT Id, Name, Email FROM {|VQ1005:Userz|}\"".Replace("{|VQ1005:", "").Replace("|}", "")), null,
            ("/p/db/shop.vqschema.json", SqlServerSnapshot), ("/p/db\\shop.vqschema.json", SqlServerSnapshot), ("/p/other/copy.vqschema.json", SqlServerSnapshot));
        Assert.DoesNotContain(messages, m => m.StartsWith("VQ1031", System.StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("VQ1005", System.StringComparison.Ordinal));
    }

    [Fact]
    public void The_recognizer_never_throws_on_partial_or_malformed_sql()
    {
        // Every prefix of a set of valid statements: truncated SQL is what an IDE sees while the user types.
        string[] statements =
        [
            "SELECT TOP (10) c.Id, c.Name, o.Total FROM dbo.Customers c WITH (NOLOCK) LEFT JOIN Orders AS o ON o.CustomerId = c.Id WHERE o.Status = @s ORDER BY o.Total DESC OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY",
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 5) SELECT x, (SELECT COUNT(*) FROM t WHERE t.a = n.x) AS c FROM n FOR UPDATE",
            "INSERT INTO products AS p (id, name) OVERRIDING SYSTEM VALUE VALUES (1, @n) ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name WHERE p.id > 0 RETURNING id, name",
            "UPDATE o SET Status = @s OUTPUT INSERTED.Id FROM Orders o JOIN Customers c ON c.Id = o.CustomerId WHERE c.Name = @n",
            "DELETE t1, t2 FROM t1 JOIN t2 USING (id) WHERE t1.x IN (SELECT y FROM z) ORDER BY t1.id LIMIT 3",
            "SELECT * FROM ONLY products p NATURAL JOIN stock CROSS JOIN LATERAL (SELECT * FROM f(p.id)) x(a, b) TABLESAMPLE SYSTEM (10)",
            "DECLARE @n int = CASE WHEN @p = 1 THEN 1 END, @m int; SELECT @n FOR XML PATH",
            "MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET t.x = s.x;",
        ];
        foreach (var dialect in new[] { VelQuery.Generators.Sql.SqlDialect.SqlServer, VelQuery.Generators.Sql.SqlDialect.PostgreSql, VelQuery.Generators.Sql.SqlDialect.MySql, VelQuery.Generators.Sql.SqlDialect.Sqlite })
        {
            foreach (var sql in statements)
            {
                for (var length = 0; length <= sql.Length; length++)
                {
                    var prefix = sql.Substring(0, length);
                    var ex = Record.Exception(() => VelQuery.Generators.Schema.QueryShapeBuilder.Build(prefix, dialect, null, null, out _));
                    Assert.True(ex is null, $"{dialect}: '{prefix}' threw {ex}");
                }
            }
        }
    }

    // ---------------------------------------------------------------- VQ1011 and VQ1031

    [Fact]
    public void VQ1011_for_sql_outside_the_checked_subset()
        => SqlServer(
            Contract("{|VQ1011:\"MERGE Users AS t USING (SELECT 1 AS Id) s ON t.Id = s.Id WHEN MATCHED THEN DELETE;\"|}", "Task<int>", name: "A", cardinality: "NonQuery")
            + Contract("{|VQ1011:\"SELECT * FROM Users FOR JSON PATH\"|}", name: "B"));

    [Fact]
    public void VQ1011_when_snapshot_dialect_differs_from_the_query_dialect()
        => DiagnosticVerifier.Verify(Rows + Contract("{|VQ1011:\"SELECT Id, Name, Email FROM Userz\"|}"), null, ("/s.vqschema.json", PostgresSnapshot));

    [Fact]
    public void VQ1031_for_an_invalid_or_ambiguous_snapshot()
    {
        var invalid = DiagnosticVerifier.Messages(Rows + Contract("\"SELECT Id, Name, Email FROM Userz\""), null, ("/s.vqschema.json", "{ \"format\": "));
        Assert.Equal(["VQ1031: Schema snapshot 's.vqschema.json' is not used: it is not valid JSON (unexpected end of file at line 1)"], invalid);

        var two = DiagnosticVerifier.Messages(Rows, null, ("/a.vqschema.json", SqlServerSnapshot), ("/b.vqschema.json", SqlServerSnapshot.Replace("\"Users\"", "\"People\"")));
        Assert.Single(two, m => m.StartsWith("VQ1031", System.StringComparison.Ordinal) && m.Contains("(a.vqschema.json, b.vqschema.json)"));

        var format = DiagnosticVerifier.Messages(Rows, null, ("/s.vqschema.json", SqlServerSnapshot.Replace("velquery.schema.v1", "v9")));
        Assert.Single(format, m => m.Contains("'format' must be \"velquery.schema.v1\""));
    }

    [Fact]
    public void Without_a_snapshot_nothing_is_reported()
        => DiagnosticVerifier.Verify(Rows + Contract("\"SELECT Nope FROM Missing\""));

    [Fact]
    public void Snapshot_changes_do_not_regenerate_code()
    {
        var compilation = Testing.GeneratorHarness.Compile(DiagnosticVerifierUsings + Rows + Contract("\"SELECT Id, Name, Email FROM Users\""));
        var original = new TestText("/s.vqschema.json", SqlServerSnapshot);
        Microsoft.CodeAnalysis.GeneratorDriver driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(
            [Microsoft.CodeAnalysis.GeneratorExtensions.AsSourceGenerator(new VelQuery.Generators.VelQuerySqlGenerator())],
            [original],
            Testing.GeneratorHarness.ParseOptions,
            null,
            new Microsoft.CodeAnalysis.GeneratorDriverOptions(Microsoft.CodeAnalysis.IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);
        Assert.DoesNotContain(driver.GetRunResult().Results.Single().Diagnostics, d => d.Id.StartsWith("VQ", System.StringComparison.Ordinal));

        driver = driver.ReplaceAdditionalText(original, new TestText("/s.vqschema.json", SqlServerSnapshot.Replace("\"Email\"", "\"Mail\""))).RunGenerators(compilation);
        var run = driver.GetRunResult().Results.Single();
        Assert.All(run.TrackedSteps[VelQuery.Generators.VelQuerySqlGenerator.TrackingNames.Models].SelectMany(s => s.Outputs),
            o => Assert.True(o.Reason is Microsoft.CodeAnalysis.IncrementalStepRunReason.Cached or Microsoft.CodeAnalysis.IncrementalStepRunReason.Unchanged, o.Reason.ToString()));
        Assert.Contains(run.Diagnostics, d => d.Id == "VQ1005");
    }

    private const string DiagnosticVerifierUsings = "using System; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks; using VelQuery;\n";

    private sealed class TestText(string path, string text) : Microsoft.CodeAnalysis.AdditionalText
    {
        public override string Path { get; } = path;

        public override Microsoft.CodeAnalysis.Text.SourceText GetText(System.Threading.CancellationToken cancellationToken = default)
            => Microsoft.CodeAnalysis.Text.SourceText.From(text);
    }
}
