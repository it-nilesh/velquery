using System.Linq;
using VelQuery.Generators.Sql;
using VelQuery.Testing;
using Xunit;

namespace VelQuery.Analyzers.Tests;

/// <summary>Dialect-aware placeholder scanning and diagnostics (VQ-DB-008).</summary>
public sealed class SqlDialectTests
{
    private static string Names(string sql, SqlDialect dialect)
    {
        var result = SqlParameterScanner.Scan(sql, dialect);
        Assert.Null(result.Unterminated);
        return string.Join(",", result.Placeholders.Select(p => p.Name));
    }

    private static string Foreign(string sql, SqlDialect dialect)
        => string.Join(",", SqlParameterScanner.Scan(sql, dialect).Foreign.Select(p => p.Name));

    [Theory]
    [InlineData(@"SELECT 'it\'s @x', @Real", "Real")]
    [InlineData(@"SELECT ""it\""s @x"", ""a@b"", @Real", "Real")]
    [InlineData("SELECT 1 # it's @x\n, @Real", "Real")]
    [InlineData("SELECT 1 -- @x\n, @Real", "Real")]
    [InlineData("SELECT 5--@Real", "Real")]
    [InlineData("SELECT `a@b`, `it's`, @Real", "Real")]
    [InlineData("SELECT /*! @Real */ 1", "Real")]
    [InlineData("SELECT /* /* @x */ @Real", "Real")]
    [InlineData("SET @rownum := 0; SELECT @rownum := @rownum + 1 AS n, @Real", "Real")]
    [InlineData("SET @v = 5; SELECT x INTO @w FROM t; SELECT @v, @w, @Real", "Real")]
    [InlineData("SELECT @@session.sql_mode, @@GLOBAL.x, @Real", "Real")]
    public void MySql_rules(string sql, string expected) => Assert.Equal(expected, Names(sql, SqlDialect.MySql));

    [Theory]
    [InlineData("DECLARE @n int = 1; SELECT Id FROM t WHERE Id >= @n AND Name = @Real", "Real")]
    [InlineData("DECLARE @a int = @Limit, @b nvarchar(10) = N'x'; SELECT @a, @B, @Real", "Limit,Real")]
    [InlineData("DECLARE @t TABLE (Id int); INSERT INTO @t SELECT Id FROM x WHERE y = @Real; SELECT * FROM @t", "Real")]
    [InlineData("DECLARE @n int = (SELECT COUNT(*) FROM t WHERE k = @Real) SELECT @n", "Real")]
    [InlineData("DECLARE c CURSOR FOR SELECT Id FROM t WHERE k = @Real", "Real")]
    [InlineData("SELECT Id FROM t WHERE Id = @Real; DECLARE @late int; SET @late = 1", "Real")]
    [InlineData("DECLARE @a int = CASE WHEN @Real = 1 THEN 1 END, @b int = 0; SELECT @a + @b", "Real")]
    public void SqlServer_declared_variables_are_not_parameters(string sql, string expected)
        => Assert.Equal(expected, Names(sql, SqlDialect.SqlServer));

    [Theory]
    [InlineData("SELECT $$ it's @x $$, @Real", "Real")]
    [InlineData("SELECT $fn$ @x $ $fn$, @Real", "Real")]
    [InlineData(@"SELECT E'it\'s @x', e'@y', @Real", "Real")]
    [InlineData("SELECT ARRAY[@a, @b], tags[@c]", "a,b,c")]
    [InlineData("SELECT x::int, @Real::text", "Real")]
    [InlineData("SELECT to_tsvector(body) @@ to_tsquery(@q)", "q")]
    [InlineData("SELECT doc ? 'key', doc ?| array['a'], @Real", "Real")]
    [InlineData("SELECT 1 /* a /* @x */ @y */ , @Real", "Real")]
    [InlineData("SELECT \"@x\", @Real", "Real")]
    public void PostgreSql_rules(string sql, string expected) => Assert.Equal(expected, Names(sql, SqlDialect.PostgreSql));

    [Theory]
    [InlineData("SELECT [@x], `@y`, \"@z\", @Real", "Real")]
    [InlineData("SELECT 1 /* /* @x */ @Real", "Real")]
    [InlineData("SELECT json_extract(doc, '$.a@b'), @Real", "Real")]
    public void Sqlite_rules(string sql, string expected) => Assert.Equal(expected, Names(sql, SqlDialect.Sqlite));

    [Theory]
    [InlineData("SELECT * FROM t WHERE a = ? AND b = ?2 AND c = :name AND d = $var", (int)SqlDialect.Sqlite, "?,?2,:name,$var")]
    [InlineData("SELECT * FROM t WHERE a = ? AND b = :name AND c := 1", (int)SqlDialect.MySql, "?,:name")]
    [InlineData("SELECT * FROM t WHERE a = $1 AND b = $2 AND doc ? 'k' AND x::int = 1", (int)SqlDialect.PostgreSql, "$1,$2")]
    [InlineData("SELECT * FROM t WHERE a = ? AND b = :name", (int)SqlDialect.SqlServer, "")]
    public void Foreign_placeholder_styles_are_reported(string sql, int dialect, string expected)
        => Assert.Equal(expected, Foreign(sql, (SqlDialect)dialect));

    [Theory]
    [InlineData("SELECT $tag$ @x", (int)SqlDialect.PostgreSql, "dollar-quoted string")]
    [InlineData(@"SELECT 'it\'s", (int)SqlDialect.MySql, "string literal")]
    [InlineData(@"SELECT E'x\'", (int)SqlDialect.PostgreSql, "string literal")]
    [InlineData("SELECT `a", (int)SqlDialect.MySql, "quoted identifier")]
    public void Unterminated_dialect_constructs(string sql, int dialect, string kind)
        => Assert.Equal(kind, SqlParameterScanner.Scan(sql, (SqlDialect)dialect).Unterminated);

    [Fact]
    public void SqlServer_default_is_unchanged_for_the_constructs_other_dialects_treat_differently()
    {
        // T-SQL: brackets are identifiers, backticks/#/$/E'' are ordinary characters, comments nest.
        Assert.Equal("Real,b", Names(@"SELECT [@x], `a`, #t, $1, E'\', @Real, @b", SqlDialect.SqlServer));
        Assert.Equal("Real", Names("SELECT 1 /* /* @x */ @y */ , @Real", SqlDialect.SqlServer));
    }

    [Theory]
    [InlineData("sqlserver", (int)SqlDialect.SqlServer)]
    [InlineData("MSSQL", (int)SqlDialect.SqlServer)]
    [InlineData("", (int)SqlDialect.SqlServer)]
    [InlineData(" postgres ", (int)SqlDialect.PostgreSql)]
    [InlineData("MariaDB", (int)SqlDialect.MySql)]
    [InlineData("sqlite", (int)SqlDialect.Sqlite)]
    [InlineData("generic", (int)SqlDialect.Generic)]
    public void Dialect_names_parse(string name, int expected)
    {
        Assert.True(SqlDialects.TryParse(name, out var dialect));
        Assert.Equal((SqlDialect)expected, dialect);
    }

    private const string Contract = """
        using System.Threading;
        using System.Threading.Tasks;
        using VelQuery;

        public sealed record Row(long Id, string Name);

        [VelQuerySql("q", SQL)]
        public static partial class Q
        {
            public static partial Task<System.Collections.Generic.IReadOnlyList<Row>> ExecuteAsync(IVelQuerySession session, long id, CancellationToken cancellationToken = default);
        }
        """;

    private static string[] Ids(string sqlLiteral, string? dialect, string attributeExtra = "")
    {
        var source = Contract.Replace("SQL)]", sqlLiteral + attributeExtra + ")]", System.StringComparison.Ordinal);
        return GeneratorHarness.GetVelQueryDiagnostics(GeneratorHarness.Compile(source), dialect).Select(d => d.Id).OrderBy(i => i).ToArray();
    }

    [Fact]
    public void Project_dialect_removes_false_positives_and_unknown_dialect_warns()
    {
        const string MySqlSql = "\"SELECT Id, Name FROM t WHERE Id = @id # don't @x\"";
        Assert.Contains("VQ1019", Ids(MySqlSql, null));
        Assert.Empty(Ids(MySqlSql, "mysql"));
        Assert.Equal(["VQ1024"], Ids("\"SELECT Id, Name FROM t WHERE Id = @id\"", "oracle"));
    }

    [Fact]
    public void Attribute_dialect_overrides_the_project_dialect()
    {
        const string MySqlSql = "\"SELECT Id, Name FROM t WHERE Id = @id # don't @x\"";
        Assert.Empty(Ids(MySqlSql, "sqlserver", ", Dialect = \"mysql\""));
        Assert.Equal(["VQ1024"], Ids("\"SELECT Id, Name FROM t WHERE Id = @id\"", null, ", Dialect = \"db2\""));
    }

    [Fact]
    public void Foreign_placeholders_and_inconsistent_casing_warn()
    {
        Assert.Equal(["VQ1022"], Ids("\"SELECT Id, Name FROM t WHERE Id = @id OR Id = :id\"", "sqlite"));
        Assert.Equal(["VQ1023"], Ids("\"SELECT Id, Name FROM t WHERE Id = @Id OR Parent = @id\"", "sqlite"));
    }

    [Fact]
    public void Sqlite_binds_every_spelling_while_case_insensitive_dialects_bind_one()
    {
        var source = Contract.Replace("SQL)]", "\"SELECT Id, Name FROM t WHERE Id = @Id OR Parent = @id\")]", System.StringComparison.Ordinal);

        string Generated(string dialect)
        {
            var (result, _) = GeneratorHarness.Run(GeneratorHarness.Compile(source), dialect);
            return string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
        }

        var sqlite = Generated("sqlite");
        Assert.Contains("ParameterSpec(\"Id\"", sqlite, System.StringComparison.Ordinal);
        Assert.Contains("ParameterSpec(\"id\"", sqlite, System.StringComparison.Ordinal);

        var sqlServer = Generated("sqlserver");
        Assert.Contains("ParameterSpec(\"Id\"", sqlServer, System.StringComparison.Ordinal);
        Assert.DoesNotContain("ParameterSpec(\"id\"", sqlServer, System.StringComparison.Ordinal);
    }
}
