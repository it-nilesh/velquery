using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

/// <summary>VQ-FR-009/010, PRD §8.2 and §13: provider timeouts versus caller cancellation.</summary>
public sealed class TimeoutAndCancellationTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    private const string SlowSql = "WAITFOR DELAY '00:00:05'; SELECT 1;";

    [Fact]
    public async Task Per_call_timeout_raises_VelQueryTimeoutException_with_sql_timeout_inner()
    {
        await using var session = Session();
        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<VelQueryTimeoutException>(
            () => session.Sql(SlowSql).WithTimeout(TimeSpan.FromSeconds(1)).ExecuteScalarAsync<int>(Ct));
        stopwatch.Stop();

        Assert.Equal(VelQueryErrorCategory.Timeout, ex.Category);
        Assert.Equal(-2, Assert.IsType<SqlException>(ex.InnerException).Number);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4.5), $"timeout took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Timeout_applies_to_every_operation_kind()
    {
        await using var session = Session();
        var command = session.Sql(SlowSql).WithTimeout(TimeSpan.FromSeconds(1));

        await AssertTimeout(() => command.ExecuteAsync(Ct));
        await AssertTimeout(() => command.QueryAsync<int>(Ct));
        await AssertTimeout(() => command.QuerySingleAsync<int>(Ct));
        await AssertTimeout(async () =>
        {
            await foreach (var _ in command.StreamAsync<int>(Ct))
            {
            }
        });
        await AssertTimeout(async () =>
        {
            await using var reader = await command.QueryMultipleAsync(Ct);
            await reader.ReadAsync<int>(Ct);
        });

        static async Task AssertTimeout(Func<Task> action)
        {
            var ex = await Assert.ThrowsAsync<VelQueryTimeoutException>(action);
            Assert.Equal(-2, Assert.IsType<SqlException>(ex.InnerException).Number);
        }
    }

    [Fact]
    public async Task Session_and_options_timeouts_apply_and_per_call_overrides_them()
    {
        await using var viaOptions = Session(new VelQueryOptions { DefaultTimeout = TimeSpan.FromSeconds(1), AllowDynamicFallback = true });
        await Assert.ThrowsAsync<VelQueryTimeoutException>(() => viaOptions.ExecuteAsync(SlowSql, cancellationToken: Ct));

        await using var viaSession = Session();
        viaSession.CommandTimeout = TimeSpan.FromSeconds(1);
        await Assert.ThrowsAsync<VelQueryTimeoutException>(() => viaSession.ExecuteAsync(SlowSql, cancellationToken: Ct));

        // Per-call timeout wins over the 1s session default.
        Assert.Equal(1, await viaSession.Sql("WAITFOR DELAY '00:00:01.500'; SELECT 1;").WithTimeout(TimeSpan.FromSeconds(10)).ExecuteScalarAsync<int>(Ct));
    }

    [Fact]
    public async Task Caller_cancellation_during_waitfor_throws_OperationCanceledException_quickly()
    {
        await using var session = Session();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(SlowSql, cancellationToken: cts.Token));
        stopwatch.Stop();

        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"cancellation took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_timeout_for_any_operation_kind()
    {
        await using var session = Session();
        await AssertCancelled(t => session.QueryAsync<int>(SlowSql, cancellationToken: t));
        await AssertCancelled(t => session.ExecuteScalarAsync<int>(SlowSql, cancellationToken: t));
        await AssertCancelled(t => session.QueryFirstAsync<int>(SlowSql, cancellationToken: t));
        await AssertCancelled(async t =>
        {
            await using var reader = await session.QueryMultipleAsync(SlowSql, cancellationToken: t);
            await reader.ReadAsync<int>(t);
        });

        static async Task AssertCancelled(Func<CancellationToken, Task> action)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(250));
            var stopwatch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action(cts.Token));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"cancellation took {stopwatch.Elapsed}");
        }
    }

    [Fact]
    public async Task Already_cancelled_token_sends_no_command()
    {
        var marker = "precancel-" + Guid.NewGuid().ToString("N");
        await using var session = Session();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(
            "INSERT dbo.Scratch (Marker) VALUES (@Marker)", new QueryParameters().Add("Marker", marker), cts.Token));
        Assert.Equal(0, await session.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Scratch WHERE Marker = @Marker", new QueryParameters().Add("Marker", marker), Ct));
    }

    [Fact]
    public async Task Cancellation_during_streaming_ReadAsync_stops_enumeration_quickly()
    {
        // 10M rows: far more than can be buffered, so later ReadAsync calls must go to the network.
        const string Huge = "SELECT a.Id, a.Name FROM dbo.Rows100k a CROSS JOIN dbo.Rows100 b";
        await using var session = Session();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var read = 0;
        var stopwatch = new Stopwatch();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in session.StreamAsync<IdName>(Huge, cancellationToken: cts.Token))
            {
                if (++read == 1000)
                {
                    stopwatch.Start();
                    await cts.CancelAsync();
                }
            }
        });
        stopwatch.Stop();

        Assert.True(read >= 1000);
        Assert.True(read < 10_000_000);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"cancellation took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Cancellation_via_WithCancellation_on_stream_is_honored()
    {
        await using var session = Session();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in session.StreamAsync<int>(SlowSql, cancellationToken: Ct).WithCancellation(cts.Token))
            {
            }
        });
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"cancellation took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Cancellation_during_connection_open_releases_nothing_and_throws_cancellation()
    {
        var dataSource = IsolatedPoolDataSource(1, "open-cancel");
        await using var holder = dataSource.CreateSession();

        // Hold the only pooled connection inside a transaction so the next open must wait on the pool.
        await using (var tx = await holder.BeginTransactionAsync(cancellationToken: Ct))
        {
            await using var waiter = dataSource.CreateSession();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(300));
            var stopwatch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: cts.Token));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"open cancellation took {stopwatch.Elapsed}");
        }

        // The transaction scope released the connection; the pool slot is usable again.
        await using var after = dataSource.CreateSession();
        Assert.Equal(1, await after.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
    }
}
