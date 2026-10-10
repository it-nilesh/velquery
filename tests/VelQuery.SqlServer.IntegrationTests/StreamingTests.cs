using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

/// <summary>VQ-FR-013, VQ-FR-027, PRD §18.1: incremental streaming, early exit and bounded memory.</summary>
public sealed class StreamingTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    // ~10M rows; draining it would take far longer than any assertion below allows.
    private const string Huge = "SELECT a.Id, a.Name FROM dbo.Rows100k a CROSS JOIN dbo.Rows100 b";

    [Fact]
    public async Task Streams_all_100k_rows_in_order()
    {
        await using var session = Session();
        long expected = 1;
        await foreach (var row in session.StreamAsync(Descriptors.Rows100kAll, default, Ct))
        {
            Assert.Equal(expected, row.Id);
            expected++;
        }

        Assert.Equal(SqlServerFixture.LargeRowCount + 1, expected);
    }

    [Fact]
    public async Task Empty_stream_completes()
    {
        await using var session = Session();
        var count = 0;
        await foreach (var _ in session.StreamAsync(Descriptors.Rows100UpTo, 0L, Ct))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Rows_are_yielded_before_the_result_is_complete()
    {
        await using var session = Session();
        var stopwatch = Stopwatch.StartNew();
        var seen = 0;
        await foreach (var _ in session.StreamAsync<IdName>(Huge, cancellationToken: Ct))
        {
            if (++seen == 10_000)
            {
                break;
            }
        }

        stopwatch.Stop();
        Assert.Equal(10_000, seen);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"first rows took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Early_break_cancels_instead_of_draining_and_returns_the_connection()
    {
        // Pool of one: if the stream kept its connection, the follow-up query could not open one.
        var dataSource = IsolatedPoolDataSource(1, "early-break");
        await using var session = dataSource.CreateSession();

        // Baseline: time to drain 100k rows.
        var drain = Stopwatch.StartNew();
        var drained = 0;
        await foreach (var _ in session.StreamAsync(Descriptors.Rows100kAll, default, Ct))
        {
            drained++;
        }

        drain.Stop();
        Assert.Equal(SqlServerFixture.LargeRowCount, drained);

        // Early exit from a 100x larger result: must cost about as much as a few rows, not a drain.
        var early = Stopwatch.StartNew();
        await foreach (var row in session.StreamAsync<IdName>(Huge, cancellationToken: Ct))
        {
            Assert.True(row.Id > 0);
            break;
        }

        early.Stop();
        var estimatedHugeDrain = drain.Elapsed * 100;
        Assert.True(early.Elapsed < TimeSpan.FromSeconds(5), $"early break took {early.Elapsed}");
        Assert.True(early.Elapsed * 10 < estimatedHugeDrain, $"early break {early.Elapsed} vs estimated drain {estimatedHugeDrain}");

        var follow = Stopwatch.StartNew();
        Assert.Equal(1, await session.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
        Assert.True(follow.Elapsed < TimeSpan.FromSeconds(3), $"connection was not returned promptly ({follow.Elapsed})");
    }

    [Fact]
    public async Task Consumer_exception_mid_stream_releases_the_connection()
    {
        var dataSource = IsolatedPoolDataSource(1, "consumer-throw");
        await using var session = dataSource.CreateSession();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var row in session.StreamAsync<IdName>(Huge, cancellationToken: Ct))
            {
                if (row.Id == 37)
                {
                    throw new InvalidOperationException("consumer failure");
                }
            }
        });

        Assert.Equal(1, await session.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
    }

    [Fact]
    public async Task Mapper_failure_on_row_37_releases_the_connection()
    {
        var dataSource = IsolatedPoolDataSource(1, "mapper-throw");
        await using var session = dataSource.CreateSession();
        var descriptor = new QueryDescriptor<NoArgs, IdCategory>(
            "it.stream.nullOn37",
            "SELECT TOP (1000) Id, CASE WHEN Id = 37 THEN NULL ELSE 1 END AS Category FROM dbo.Rows100k ORDER BY Id",
            Mappers.IdCategory);

        var seen = 0;
        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(async () =>
        {
            await foreach (var _ in session.StreamAsync(descriptor, default, Ct))
            {
                seen++;
            }
        });

        Assert.Equal(36, seen);
        Assert.Equal("Category", ex.Column);
        Assert.Equal("it.stream.nullOn37", ex.QueryId);
        Assert.Equal(1, await session.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
    }

    [Fact]
    public async Task Retained_memory_does_not_scale_with_row_count()
    {
        await using var session = Session();
        long atStart = 0;
        long atEnd = 0;
        var count = 0;
        await foreach (var _ in session.StreamAsync<IdPayload>("SELECT Id, Name, Payload FROM dbo.Rows100k ORDER BY Id", cancellationToken: Ct))
        {
            count++;
            if (count == 1_000)
            {
                atStart = RetainedBytes();
            }
            else if (count == 99_000)
            {
                atEnd = RetainedBytes();
            }
        }

        Assert.Equal(SqlServerFixture.LargeRowCount, count);

        // Each row is ~500 bytes once materialized, so buffering 98k rows would retain ~50 MB. Streaming must not.
        var growth = atEnd - atStart;
        Assert.True(growth < 8 * 1024 * 1024, $"retained heap grew by {growth:N0} bytes while streaming");

        // Contrast: the buffered API does retain every row.
        var before = RetainedBytes();
        var buffered = await session.QueryAsync<IdPayload>("SELECT Id, Name, Payload FROM dbo.Rows100k ORDER BY Id", cancellationToken: Ct);
        var after = RetainedBytes();
        Assert.Equal(SqlServerFixture.LargeRowCount, buffered.Count);
        Assert.True(after - before > 20 * 1024 * 1024, $"buffered retained only {after - before:N0} bytes");
        GC.KeepAlive(buffered);

        static long RetainedBytes()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(forceFullCollection: true);
        }
    }

    [Fact]
    public async Task Each_enumeration_executes_the_command_again()
    {
        await using var session = Session();
        var stream = session.StreamAsync(Descriptors.Rows100UpTo, 3L, Ct);
        var first = 0;
        await foreach (var _ in stream)
        {
            first++;
        }

        var second = 0;
        await foreach (var _ in stream)
        {
            second++;
        }

        Assert.Equal(3, first);
        Assert.Equal(3, second);
    }

    public sealed record IdPayload(long Id, string Name, string Payload);
}
