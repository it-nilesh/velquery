using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>PRD §9.6: overlapping operations on a pinned connection are rejected, not silently interleaved.</summary>
public sealed class SessionConcurrencyTests
{
    private const string Sql = "select v from t";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<Op> AllOps => Operations.AllData();

    private static Harness Create()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 1));
        return h;
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public async Task Undisposed_stream_on_caller_connection_blocks_other_operations(Op op)
    {
        var h = Create();
        using var connection = h.Db.OpenCallerConnection();
        await using var session = h.DataSource.CreateSession(connection);

        var stream = session.StreamAsync<int>(Sql, null, Ct).GetAsyncEnumerator(Ct);
        Assert.True(await stream.MoveNextAsync());

        var ex = await Assert.ThrowsAsync<VelQueryValidationException>(() => Operations.RunAsync(session, op, Sql, Ct));
        Assert.Equal(VelQueryErrorCategory.Validation, ex.Category);
        Assert.Single(h.Db.Commands);

        // The rejected call must not have released the stream's hold on the connection.
        await Assert.ThrowsAsync<VelQueryValidationException>(() => session.ExecuteAsync(Sql, null, Ct));

        await stream.DisposeAsync();
        await Operations.RunAsync(session, op, Sql, Ct);
        Assert.Equal(0, connection.DisposeCount);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Undisposed_multi_reader_inside_transaction_blocks_other_operations()
    {
        var h = Create();
        await using var session = h.Session();
        await using var tx = await session.BeginTransactionAsync(cancellationToken: Ct);

        var multi = await session.QueryMultipleAsync(Sql, null, Ct);
        await Assert.ThrowsAsync<VelQueryValidationException>(() => session.QueryAsync<int>(Sql, null, Ct));
        await Assert.ThrowsAsync<VelQueryValidationException>(() => session.ExecuteScalarAsync<int>(Sql, null, Ct));

        await multi.DisposeAsync();
        Assert.Equal([1], await session.QueryAsync<int>(Sql, null, Ct));
        Assert.Single(h.Db.Connections);
    }

    [Fact]
    public async Task Undisposed_stream_inside_transaction_blocks_other_operations()
    {
        var h = Create();
        await using var session = h.Session();
        await using var tx = await session.BeginTransactionAsync(cancellationToken: Ct);

        await using (var stream = session.StreamAsync<int>(Sql, null, Ct).GetAsyncEnumerator(Ct))
        {
            Assert.True(await stream.MoveNextAsync());
            await Assert.ThrowsAsync<VelQueryValidationException>(() => session.ExecuteAsync(Sql, null, Ct));
        }

        await session.ExecuteAsync(Sql, null, Ct);
    }

    [Fact]
    public async Task Factory_session_without_transaction_allows_independent_overlapping_operations()
    {
        var h = Create();
        await using var session = h.Session();

        await using var stream = session.StreamAsync<int>(Sql, null, Ct).GetAsyncEnumerator(Ct);
        Assert.True(await stream.MoveNextAsync());
        await using var multi = await session.QueryMultipleAsync(Sql, null, Ct);

        Assert.Equal([1], await session.QueryAsync<int>(Sql, null, Ct));
        Assert.Equal(3, h.Db.Connections.Count);
    }

    [Fact]
    public async Task Concurrent_reads_on_one_multi_reader_are_rejected()
    {
        var h = new Harness();
        Task? reentrant = null;
        VelQueryMultipleReader? multi = null;
        h.Db.Script(Sql, new FakeScript
        {
            OnRead = n =>
            {
                if (n == 1)
                {
                    reentrant = multi!.ReadAsync<int>(Ct);
                }
            },
        }.With(FakeResultSet.Ints("v", 2), FakeResultSet.Ints("v", 2)));
        await using var session = h.Session();

        multi = await session.QueryMultipleAsync(Sql, null, Ct);
        await using (multi)
        {
            Assert.Equal([1, 2], await multi.ReadAsync<int>(Ct));
            await Assert.ThrowsAsync<VelQueryValidationException>(() => reentrant!);
        }
    }
}
