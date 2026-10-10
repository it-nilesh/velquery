using System.Linq;
using VelQuery.Generators.Sql;
using Xunit;

namespace VelQuery.Analyzers.Tests;

/// <summary>Lexical placeholder scanning used by VQ1001/VQ1002 and the binder (VQ-FR-033).</summary>
public sealed class SqlParameterScannerTests
{
    [Theory]
    [InlineData("SELECT * FROM T WHERE Id = @Id", "Id")]
    [InlineData("SELECT * FROM T WHERE Id = @Id AND Parent = @Id", "Id,Id")]
    [InlineData("SELECT @a, @_b, @c1 FROM T", "a,_b,c1")]
    [InlineData("SELECT '@x', N'@y', 'it''s @z', @Real", "Real")]
    [InlineData("SELECT [@x], [a]]@b], \"@c\", \"a\"\"@d\", @Real", "Real")]
    [InlineData("SELECT 1 -- @x\n, @Real -- @y", "Real")]
    [InlineData("SELECT 1 /* @x /* @y */ @z */ , @Real", "Real")]
    [InlineData("SELECT @@ROWCOUNT, @@IDENTITY, @Real", "Real")]
    [InlineData("SELECT @1, @ , @Real", "Real")]
    [InlineData("SELECT Éclair FROM T WHERE Nom = @Prénom", "Prénom")]
    public void Finds_placeholders_outside_literals_identifiers_and_comments(string sql, string expected)
    {
        var result = SqlParameterScanner.Scan(sql);

        Assert.Null(result.Unterminated);
        Assert.Equal(expected, string.Join(",", result.Placeholders.Select(p => p.Name)));
    }

    [Fact]
    public void Placeholder_offsets_cover_the_at_sign_and_name()
    {
        const string Sql = "SELECT 1 WHERE Id = @Id";
        var placeholder = Assert.Single(SqlParameterScanner.Scan(Sql).Placeholders);

        Assert.Equal("@Id", Sql.Substring(placeholder.Start, placeholder.Length));
    }

    [Theory]
    [InlineData("SELECT 'abc", "string literal")]
    [InlineData("SELECT [abc", "bracketed identifier")]
    [InlineData("SELECT \"abc", "quoted identifier")]
    [InlineData("SELECT /* /* */", "block comment")]
    public void Reports_unterminated_constructs(string sql, string kind)
        => Assert.Equal(kind, SqlParameterScanner.Scan(sql).Unterminated);
}
