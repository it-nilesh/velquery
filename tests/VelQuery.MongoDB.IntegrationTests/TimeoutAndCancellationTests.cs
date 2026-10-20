using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using Xunit;

namespace VelQuery.MongoDB.IntegrationTests;

/// <summary>
/// Per-operation limits become server-side maxTimeMS (plus a client deadline for buffered reads) and surface as
/// <see cref="VelQueryTimeoutException"/>; caller cancellation surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class TimeoutAndCancellationTests(MongoFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Contract_timeout_stops_a_slow_find_on_the_server()
    {
        await using var session = Session();
        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<VelQueryTimeoutException>(() => SlowFind.ExecuteAsync(session, -1, Ct));
        Assert.Equal("big.slow", error.QueryId);
        Assert.Equal(VelQueryErrorCategory.Timeout, error.Category);
        Assert.IsType<MongoExecutionTimeoutException>(error.InnerException);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Contract_timeout_stops_a_slow_aggregation()
    {
        await using var session = Session();
        var error = await Assert.ThrowsAsync<VelQueryTimeoutException>(() => SlowAggregate.ExecuteAsync(session, Ct));
        Assert.Equal("big.slowAggregate", error.QueryId);
        Assert.IsAssignableFrom<MongoException>(error.InnerException);
    }

    [Fact]
    public async Task Contract_timeout_applies_to_streams_server_side()
    {
        await using var session = Session();
        var error = await Assert.ThrowsAsync<VelQueryTimeoutException>(async () =>
        {
            await foreach (var _ in SlowFindStreamed.ExecuteAsync(session, -1, Ct))
            {
            }
        });
        Assert.Equal("big.slow.stream", error.QueryId);
    }

    [Fact]
    public async Task Data_source_default_timeout_applies_when_the_contract_sets_none()
    {
        using var source = Fixture.CreateDataSource(options: new VelQueryMongoOptions { DefaultTimeout = TimeSpan.FromMilliseconds(250) });
        await using var session = source.CreateSession();
        await Assert.ThrowsAsync<VelQueryTimeoutException>(() => SlowFindUntimed.ExecuteAsync(session, -1, Ct));
    }

    [Fact]
    public async Task Client_deadline_reports_a_timeout_when_it_fires_first()
    {
        // A zero grace makes the client deadline race the server's maxTimeMS; either way the caller sees a timeout.
        using var source = Fixture.CreateDataSource(options: new VelQueryMongoOptions { DefaultTimeout = TimeSpan.FromMilliseconds(100), ClientDeadlineGrace = TimeSpan.Zero });
        await using var session = source.CreateSession();
        var error = await Assert.ThrowsAsync<VelQueryTimeoutException>(() => SlowFindUntimed.ExecuteAsync(session, -1, Ct));
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public async Task Caller_cancellation_stops_a_slow_query_and_the_pool_stays_usable()
    {
        using var source = Fixture.CreateDataSource(s =>
        {
            s.MaxConnectionPoolSize = 2;
            s.WaitQueueTimeout = TimeSpan.FromSeconds(5);
        });
        for (var i = 0; i < 4; i++)
        {
            await using var session = source.CreateSession();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(200));
            var stopwatch = Stopwatch.StartNew();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SlowFindUntimed.ExecuteAsync(session, -1, cts.Token));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
            Assert.True(cts.IsCancellationRequested);
            _ = error;
        }

        await using var last = source.CreateSession();
        Assert.Equal(33, await CountByStatus.ExecuteAsync(last, "inactive", Ct));
    }

    [Fact]
    public async Task Already_cancelled_token_fails_without_running()
    {
        await using var session = Session();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PeopleByStatus.ExecuteAsync(session, "active", 0, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CountByStatus.ExecuteAsync(session, "active", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InsertAccount.ExecuteAsync(session, new Account(-5, "never", 0m), cts.Token));
    }
}
