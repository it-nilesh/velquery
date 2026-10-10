using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

/// <summary>VQ-FR-021 and batch error propagation (VelQuery drains trailing results so later errors surface).</summary>
public sealed class MultiResultTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Result_sets_are_consumed_in_order()
    {
        await using var session = Session();
        await using var reader = await session.QueryMultipleAsync(
            "SELECT Id, Name FROM dbo.Rows100 WHERE Id <= @Max ORDER BY Id; SELECT COUNT(*) FROM dbo.Rows100k; SELECT Name FROM dbo.Rows1 WHERE 1 = 0;",
            new QueryParameters().Add("Max", 4L), Ct);

        Assert.Equal(0, reader.ResultIndex);
        var first = await reader.ReadAsync<IdName>(Ct);
        Assert.Equal([1L, 2L, 3L, 4L], first.Select(r => r.Id));
        Assert.Equal(SqlServerFixture.LargeRowCount, await reader.ReadSingleAsync<int>(Ct));
        Assert.Empty(await reader.ReadAsync<string>(Ct));
        Assert.False(reader.HasMoreResults);

        // Requesting a result set that does not exist is a result-shape error.
        var ex = await Assert.ThrowsAsync<VelQueryValidationException>(() => reader.ReadAsync<int>(Ct));
        Assert.Contains("result set 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Descriptor_multi_result_with_hand_written_mappers()
    {
        await using var session = Session(new VelQueryOptions());
        var descriptor = new QueryDescriptor<NoArgs>("it.multi", "SELECT Id, Name FROM dbo.Rows1; SELECT Id, Name FROM dbo.Rows100 WHERE Id = 2;");
        await using var reader = await session.QueryMultipleAsync(descriptor, default, Ct);
        Assert.Equal("only", (await reader.ReadSingleAsync(Mappers.IdName, Ct)).Name);
        Assert.Equal("row-2", (await reader.ReadSingleAsync(Mappers.IdName, Ct)).Name);
    }

    [Fact]
    public async Task Disposing_before_all_results_releases_the_connection()
    {
        var dataSource = IsolatedPoolDataSource(1, "multi-dispose");
        await using var session = dataSource.CreateSession();
        await using (var reader = await session.QueryMultipleAsync(
            "SELECT a.Id FROM dbo.Rows100k a CROSS JOIN dbo.Rows100 b; SELECT 2;", cancellationToken: Ct))
        {
            Assert.True(reader.HasMoreResults);
        }

        Assert.Equal(1, await session.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
    }

    [Fact]
    public async Task Error_in_later_statement_surfaces_from_buffered_query()
    {
        await using var session = Session();
        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(
            () => session.QueryAsync<long>("SELECT Id FROM dbo.Rows1; SELECT 1/0;", cancellationToken: Ct));
        Assert.Equal(VelQueryErrorCategory.Provider, ex.Category);
        Assert.Equal(8134, Assert.IsType<SqlException>(ex.InnerException).Number);
        Assert.DoesNotContain("Divide", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Error_in_later_statement_surfaces_from_single_and_stream()
    {
        await using var session = Session();
        await Assert.ThrowsAsync<VelQueryProviderException>(
            () => session.QuerySingleAsync<long>("SELECT Id FROM dbo.Rows1; SELECT 1/0;", cancellationToken: Ct));
        await Assert.ThrowsAsync<VelQueryProviderException>(
            () => session.QuerySingleOrDefaultAsync<long>("SELECT Id FROM dbo.Rows1; SELECT 1/0;", cancellationToken: Ct));

        var yielded = 0;
        await Assert.ThrowsAsync<VelQueryProviderException>(async () =>
        {
            await foreach (var _ in session.StreamAsync<long>("SELECT Id FROM dbo.Rows1; SELECT 1/0;", cancellationToken: Ct))
            {
                yielded++;
            }
        });
        Assert.Equal(1, yielded);
    }

    [Fact]
    public async Task Error_in_later_statement_surfaces_from_execute_scalar()
    {
        await using var session = Session();
        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(
            () => session.ExecuteScalarAsync<int>("SELECT 1; SELECT 1/0;", cancellationToken: Ct));
        Assert.Equal(8134, Assert.IsType<SqlException>(ex.InnerException).Number);
    }

    [Fact]
    public async Task Error_in_later_statement_surfaces_from_execute_and_multi_reader()
    {
        await using var session = Session();
        await Assert.ThrowsAsync<VelQueryProviderException>(
            () => session.ExecuteAsync("UPDATE dbo.Rows1 SET Name = Name; SELECT 1/0;", cancellationToken: Ct));

        await using var reader = await session.QueryMultipleAsync("SELECT 1; SELECT 1/0;", cancellationToken: Ct);
        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(async () =>
        {
            await reader.ReadAsync<int>(Ct);
            await reader.ReadAsync<int>(Ct);
        });
        Assert.Equal(8134, Assert.IsType<SqlException>(ex.InnerException).Number);
    }
}
