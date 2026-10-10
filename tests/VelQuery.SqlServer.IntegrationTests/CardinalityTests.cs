using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

/// <summary>VQ-FR-002..006 and PRD §8.1 contract table against real rows.</summary>
public sealed class CardinalityTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    [Theory]
    [InlineData("dbo.Rows1", 1)]
    [InlineData("dbo.Rows100", 100)]
    [InlineData("dbo.Rows100k", SqlServerFixture.LargeRowCount)]
    public async Task QueryAsync_returns_every_row_in_reader_order(string table, int expected)
    {
        await using var session = Session();
        var rows = await session.Sql($"SELECT Id, Name FROM {table} ORDER BY Id").QueryAsync<IdName>(Ct);

        Assert.Equal(expected, rows.Count);
        Assert.Equal(Enumerable.Range(1, expected).Select(i => (long)i), rows.Select(r => r.Id));
    }

    [Fact]
    public async Task QueryAsync_empty_result_is_empty_list()
    {
        await using var session = Session();
        var rows = await session.QueryAsync(Descriptors.Rows100UpTo, 0L, Ct);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Descriptor_path_buffers_with_hand_written_mapper()
    {
        await using var session = Session(new VelQueryOptions());
        var rows = await session.QueryAsync(Descriptors.Rows100UpTo, 5L, Ct);
        Assert.Equal(["row-1", "row-2", "row-3", "row-4", "row-5"], rows.Select(r => r.Name));
    }

    [Fact]
    public async Task First_semantics()
    {
        await using var session = Session();
        var first = await session.QueryFirstAsync(Descriptors.Rows100UpTo, 100L, Ct);
        Assert.Equal(new IdName(1, "row-1"), first);

        var one = await session.QueryFirstAsync(Descriptors.Rows100ById, 7L, Ct);
        Assert.Equal(7, one.Id);

        var ex = await Assert.ThrowsAsync<VelQueryNoRowsException>(() => session.QueryFirstAsync(Descriptors.Rows100ById, 1000L, Ct));
        Assert.Equal("it.rows100.byId", ex.QueryId);
        Assert.Equal(VelQueryErrorCategory.NoRows, ex.Category);
    }

    [Fact]
    public async Task FirstOrDefault_semantics()
    {
        await using var session = Session();
        Assert.Null(await session.QueryFirstOrDefaultAsync(Descriptors.Rows100ById, 1000L, Ct));
        Assert.Equal(1, (await session.QueryFirstOrDefaultAsync(Descriptors.Rows100UpTo, 100L, Ct))!.Id);
        Assert.Equal(0L, await session.QueryFirstOrDefaultAsync<long>("SELECT Id FROM dbo.Rows100 WHERE Id < 0", cancellationToken: Ct));
    }

    [Fact]
    public async Task Single_semantics_distinguish_zero_and_many()
    {
        await using var session = Session();
        Assert.Equal(new IdName(42, "row-42"), await session.QuerySingleAsync(Descriptors.Rows100ById, 42L, Ct));

        await Assert.ThrowsAsync<VelQueryNoRowsException>(() => session.QuerySingleAsync(Descriptors.Rows100ById, 1000L, Ct));
        var many = await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => session.QuerySingleAsync(Descriptors.Rows100UpTo, 2L, Ct));
        Assert.Equal(VelQueryErrorCategory.TooManyRows, many.Category);
    }

    [Fact]
    public async Task SingleOrDefault_semantics()
    {
        await using var session = Session();
        Assert.Null(await session.QuerySingleOrDefaultAsync(Descriptors.Rows100ById, 1000L, Ct));
        Assert.Equal(3, (await session.QuerySingleOrDefaultAsync(Descriptors.Rows100ById, 3L, Ct))!.Id);
        await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => session.QuerySingleOrDefaultAsync(Descriptors.Rows100UpTo, 100L, Ct));
    }

    [Fact]
    public async Task Runtime_sql_cardinality_with_parameters()
    {
        await using var session = Session();
        var p = new QueryParameters().Add("Id", 42L);
        Assert.Equal("row-42", (await session.QuerySingleAsync<IdName>("SELECT Id, Name FROM dbo.Rows100 WHERE Id = @Id", p, Ct)).Name);
        Assert.Equal(1L, await session.QueryFirstAsync<long>("SELECT Id FROM dbo.Rows100k ORDER BY Id", cancellationToken: Ct));
        await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => session.QuerySingleAsync<long>("SELECT Id FROM dbo.Rows100", cancellationToken: Ct));
    }

    [Fact]
    public async Task ExecuteScalar_semantics()
    {
        await using var session = Session();
        Assert.Equal(SqlServerFixture.LargeRowCount, await session.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Rows100k", cancellationToken: Ct));

        // First cell of a multi-row result; int widened to long.
        Assert.Equal(1L, await session.ExecuteScalarAsync<long>("SELECT CAST(Id AS int) FROM dbo.Rows100 ORDER BY Id", cancellationToken: Ct));

        // NULL first cell: nullable target gets null; non-nullable target fails strictly.
        Assert.Null(await session.ExecuteScalarAsync<int?>("SELECT Category FROM dbo.Rows100 WHERE Id = 10", cancellationToken: Ct));
        var nullEx = await Assert.ThrowsAsync<VelQueryMappingException>(
            () => session.ExecuteScalarAsync<int>("SELECT Category FROM dbo.Rows100 WHERE Id = 10", cancellationToken: Ct));
        Assert.Equal(typeof(int), nullEx.TargetType);

        // No rows: nullable target gets default; non-nullable target throws NoRows (distinct from NULL).
        Assert.Null(await session.ExecuteScalarAsync<string?>("SELECT Name FROM dbo.Rows100 WHERE Id < 0", cancellationToken: Ct));
        await Assert.ThrowsAsync<VelQueryNoRowsException>(
            () => session.ExecuteScalarAsync<int>("SELECT CAST(Id AS int) FROM dbo.Rows100 WHERE Id < 0", cancellationToken: Ct));

        // Lossy conversions are rejected, not coerced.
        await Assert.ThrowsAsync<VelQueryMappingException>(
            () => session.ExecuteScalarAsync<byte>("SELECT CAST(300 AS int)", cancellationToken: Ct));
        await Assert.ThrowsAsync<VelQueryMappingException>(
            () => session.ExecuteScalarAsync<int>("SELECT N'42'", cancellationToken: Ct));
    }

    [Fact]
    public async Task Execute_returns_server_reported_affected_rows()
    {
        var marker = "exec-" + Guid.NewGuid().ToString("N");
        await using var session = Session();
        var p = new QueryParameters().Add("Marker", marker);

        Assert.Equal(3, await session.ExecuteAsync(
            "INSERT dbo.Scratch (Marker) VALUES (@Marker), (@Marker), (@Marker)", p, Ct));
        Assert.Equal(3, await session.ExecuteAsync(
            "UPDATE dbo.Scratch SET Value = N'v' WHERE Marker = @Marker", new QueryParameters().Add("Marker", marker), Ct));
        Assert.Equal(0, await session.ExecuteAsync(
            "UPDATE dbo.Scratch SET Value = N'v' WHERE Marker = @Marker", new QueryParameters().Add("Marker", "none-" + marker), Ct));

        // Multiple DML statements: SQL Server reports the sum.
        Assert.Equal(4, await session.ExecuteAsync(
            "UPDATE dbo.Scratch SET Value = N'w' WHERE Marker = @Marker; INSERT dbo.Scratch (Marker) VALUES (@Marker);",
            new QueryParameters().Add("Marker", marker), Ct));

        Assert.Equal(4, await session.ExecuteAsync(
            "DELETE dbo.Scratch WHERE Marker = @Marker", new QueryParameters().Add("Marker", marker), Ct));
    }

    [Fact]
    public async Task Execute_returns_minus_one_unchanged_when_server_reports_no_count()
    {
        await using var session = Session();
        var marker = "nocount-" + Guid.NewGuid().ToString("N");

        // SET NOCOUNT ON suppresses DONE_COUNT; SqlClient reports -1 and VelQuery must not invent a count.
        Assert.Equal(-1, await session.ExecuteAsync(
            "SET NOCOUNT ON; INSERT dbo.Scratch (Marker) VALUES (@Marker), (@Marker);",
            new QueryParameters().Add("Marker", marker), Ct));
        Assert.Equal(2, await session.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Scratch WHERE Marker = @Marker", new QueryParameters().Add("Marker", marker), Ct));

        // A pure SELECT or a non-DML statement also reports -1.
        Assert.Equal(-1, await session.ExecuteAsync("SELECT 1", cancellationToken: Ct));
        Assert.Equal(-1, await session.ExecuteAsync("DECLARE @x int = 1;", cancellationToken: Ct));
    }
}
