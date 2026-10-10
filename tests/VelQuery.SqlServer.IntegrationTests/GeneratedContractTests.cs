using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

// Generated contracts against real SqlClient. These rows use CommandBehavior.SequentialAccess, so SqlClient itself
// rejects any out-of-order column read; distinct row types keep them from replacing mappers other tests rely on.

public sealed record GenRow(long Id, string Name, int? Category);

public sealed record GenStrictRow(long Id, string Name, int Category);

public sealed record GenLargeRow(long Id, string Name, string Payload);

[VelQuerySql("gen.rows100", "SELECT Id, Name, Category FROM dbo.Rows100 ORDER BY Id")]
public static partial class GenRows100
{
    public static partial Task<IReadOnlyList<GenRow>> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("gen.rows100-shuffled", "SELECT Category, Name, Id FROM dbo.Rows100 ORDER BY Id")]
public static partial class GenRows100Shuffled
{
    public static partial IAsyncEnumerable<GenRow> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("gen.rows100-strict", "SELECT Id, Name, Category FROM dbo.Rows100 ORDER BY Id")]
public static partial class GenRows100Strict
{
    public static partial Task<IReadOnlyList<GenStrictRow>> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("gen.rows100k", "SELECT Id, Name, Payload FROM dbo.Rows100k ORDER BY Id")]
public static partial class GenRows100k
{
    public static partial IAsyncEnumerable<GenLargeRow> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

public sealed class GeneratedContractTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Generated_mapper_reads_real_rows_with_sequential_access()
    {
        await using var session = Session();

        var rows = await GenRows100.ExecuteAsync(session, Ct);

        Assert.Equal(100, rows.Count);
        Assert.Equal(new GenRow(1, "row-1", 1), rows[0]);
        Assert.Equal(new GenRow(10, "row-10", null), rows[9]);
    }

    [Fact]
    public async Task Shuffled_select_order_maps_identically_under_sequential_access()
    {
        await using var session = Session();
        var expected = await GenRows100.ExecuteAsync(session, Ct);

        var shuffled = new List<GenRow>();
        await foreach (var row in GenRows100Shuffled.ExecuteAsync(session, Ct))
        {
            shuffled.Add(row);
        }

        Assert.Equal(expected, shuffled);
    }

    [Fact]
    public async Task Strict_null_into_non_nullable_member_reports_column_from_sqlclient_fast_path()
    {
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => GenRows100Strict.ExecuteAsync(session, Ct));

        Assert.Equal("Category", ex.Column);
        Assert.Equal(typeof(int), ex.TargetType);
        Assert.Equal("gen.rows100-strict", ex.QueryId);
    }

    [Fact]
    public async Task Non_strict_session_maps_null_to_default_with_sqlclient()
    {
        await using var session = Session(new VelQueryOptions { StrictMapping = false });

        var rows = await GenRows100Strict.ExecuteAsync(session, Ct);

        Assert.Equal(0, rows[9].Category);
        Assert.Equal(1, rows[0].Category);
    }

    [Fact]
    public async Task Generated_stream_reads_100k_rows_and_early_exit_releases_the_connection()
    {
        await using var dataSource = IsolatedPoolDataSource(maxPoolSize: 1, poolTag: "gen-stream");
        await using (var session = dataSource.CreateSession())
        {
            long count = 0;
            await foreach (var row in GenRows100k.ExecuteAsync(session, Ct))
            {
                Assert.Equal(++count, row.Id);
            }

            Assert.Equal(SqlServerFixture.LargeRowCount, count);

            await foreach (var _ in GenRows100k.ExecuteAsync(session, Ct))
            {
                break;
            }
        }

        // With a pool of one, this only succeeds if the early-exited stream returned its connection.
        await using var next = dataSource.CreateSession();
        Assert.Equal(100, (await GenRows100.ExecuteAsync(next, Ct)).Count);
    }
}
