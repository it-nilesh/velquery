using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

/// <summary>VQ-FR-015..017 against real result schemas, for hand-written and reflection-fallback mappers.</summary>
public sealed class StrictMappingTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    private const string NullCategory = "SELECT Id, Category FROM dbo.Rows100 WHERE Id = 10";

    [Fact]
    public async Task Null_into_non_nullable_member_fails_with_column_name()
    {
        await using var session = Session();

        var viaDescriptor = await Assert.ThrowsAsync<VelQueryMappingException>(() => session.QuerySingleAsync(
            new QueryDescriptor<NoArgs, IdCategory>("it.strict.null", NullCategory, Mappers.IdCategory), default, Ct));
        Assert.Equal("Category", viaDescriptor.Column);
        Assert.Equal(typeof(int), viaDescriptor.TargetType);
        Assert.Equal("it.strict.null", viaDescriptor.QueryId);
        Assert.Equal(VelQueryErrorCategory.Mapping, viaDescriptor.Category);

        var viaReflection = await Assert.ThrowsAsync<VelQueryMappingException>(
            () => session.Sql(NullCategory).WithQueryId("it.strict.reflect").QuerySingleAsync<IdCategory>(Ct));
        Assert.Equal("Category", viaReflection.Column);
        Assert.Equal("it.strict.reflect", viaReflection.QueryId);
        Assert.Contains("Category", viaReflection.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_into_nullable_member_succeeds_and_lenient_mode_defaults()
    {
        await using var session = Session();
        var row = await session.QuerySingleAsync<NullableCategory>(NullCategory, cancellationToken: Ct);
        Assert.Null(row.Category);

        await using var lenient = Session(new VelQueryOptions { StrictMapping = false, AllowDynamicFallback = true });
        var defaulted = await lenient.QuerySingleAsync<IdCategory>(NullCategory, cancellationToken: Ct);
        Assert.Equal(0, defaulted.Category);
    }

    [Fact]
    public async Task Missing_column_fails_before_reading_rows()
    {
        await using var session = Session();
        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(
            () => session.QueryAsync<IdName>("SELECT Id FROM dbo.Rows100", cancellationToken: Ct));
        Assert.Equal("Name", ex.Column);

        var viaDescriptor = await Assert.ThrowsAsync<VelQueryMappingException>(() => session.QueryAsync(
            new QueryDescriptor<NoArgs, IdName>("it.strict.missing", "SELECT Id FROM dbo.Rows100", Mappers.IdName), default, Ct));
        Assert.Equal("Name", viaDescriptor.Column);
    }

    [Fact]
    public async Task Extra_columns_are_ignored_and_aliases_match_case_insensitively()
    {
        await using var session = Session();
        var rows = await session.QueryAsync<IdName>(
            "SELECT Name AS NAME, N'extra' AS Extra, Id AS id, Category FROM dbo.Rows100 WHERE Id <= 3 ORDER BY Id", cancellationToken: Ct);
        Assert.Equal(["row-1", "row-2", "row-3"], rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Duplicate_column_alias_is_rejected_as_ambiguous()
    {
        await using var session = Session();
        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(
            () => session.QueryAsync<IdName>("SELECT Id, Id, Name FROM dbo.Rows100", cancellationToken: Ct));
        Assert.Equal("Id", ex.Column);
    }

    [Fact]
    public async Task Lossy_type_conversion_fails_without_echoing_values()
    {
        await using var session = Session();
        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(
            () => session.QueryAsync<NarrowId>("SELECT CAST(9223372036854775807 AS bigint) AS Id", cancellationToken: Ct));
        Assert.Equal("Id", ex.Column);
        Assert.DoesNotContain("9223372036854775807", ex.Message, System.StringComparison.Ordinal);

        // A string column is never parsed into a number.
        await Assert.ThrowsAsync<VelQueryMappingException>(
            () => session.QueryAsync<NarrowId>("SELECT N'1' AS Id", cancellationToken: Ct));
    }

    public sealed record NullableCategory(long Id, int? Category);

    public sealed record NarrowId(int Id);
}
