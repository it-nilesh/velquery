using System;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace VelQuery.MongoDB.IntegrationTests;

/// <summary>Streams own their cursor: released exactly once on completion, early exit, failure and cancellation (VQ-FR-013).</summary>
public sealed class StreamingTests(MongoFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Streams_every_document_in_order()
    {
        await using var session = Session();
        var expected = 1;
        await foreach (var row in StreamBig.ExecuteAsync(session, Ct))
        {
            Assert.Equal(expected++, row.Id);
        }

        Assert.Equal(MongoFixture.BigCount + 1, expected);
    }

    [Fact]
    public async Task Early_exit_kills_the_server_cursor()
    {
        var before = await OpenCursorsAsync();
        await using (var session = Session())
        {
            for (var i = 0; i < 10; i++)
            {
                var taken = 0;
                await foreach (var row in StreamBig.ExecuteAsync(session, Ct))
                {
                    if (++taken == 150)
                    {
                        break; // Past the first batch: a live server cursor exists.
                    }
                }
            }
        }

        Assert.Equal(before, await OpenCursorsAsync());
    }

    [Fact]
    public async Task Early_exits_do_not_leak_pooled_connections()
    {
        using var source = Fixture.CreateDataSource(s =>
        {
            s.MaxConnectionPoolSize = 2;
            s.WaitQueueTimeout = TimeSpan.FromSeconds(2);
        });
        for (var i = 0; i < 50; i++)
        {
            await using var session = source.CreateSession();
            await using var enumerator = StreamBig.ExecuteAsync(session, Ct).GetAsyncEnumerator(Ct);
            Assert.True(await enumerator.MoveNextAsync());
        }

        await using var last = source.CreateSession();
        Assert.Equal(33, await CountByStatus.ExecuteAsync(last, "inactive", Ct));
    }

    [Fact]
    public async Task Consumer_exception_releases_the_cursor()
    {
        var before = await OpenCursorsAsync();
        await using var session = Session();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var row in StreamBig.ExecuteAsync(session, Ct))
            {
                if (row.Id == 500)
                {
                    throw new InvalidOperationException("consumer failure");
                }
            }
        });

        Assert.Equal(before, await OpenCursorsAsync());
    }

    [Fact]
    public async Task Cancellation_mid_stream_throws_and_releases_the_cursor()
    {
        var before = await OpenCursorsAsync();
        await using var session = Session();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var row in StreamBig.ExecuteAsync(session, cts.Token))
            {
                if (++seen == 300)
                {
                    await cts.CancelAsync();
                }
            }
        });

        Assert.True(seen < MongoFixture.BigCount);
        Assert.Equal(before, await OpenCursorsAsync());
    }

    [Fact]
    public async Task WithCancellation_token_is_honored_together_with_the_method_token()
    {
        await using var session = Session();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in StreamBig.ExecuteAsync(session, Ct).WithCancellation(cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Stream_is_lazy_and_can_be_enumerated_again()
    {
        await using var session = Session();
        var stream = OldestPeople.ExecuteAsync(session, "active", 2, Ct);
        var first = await System.Linq.AsyncEnumerable.ToListAsync(stream, Ct);
        var second = await System.Linq.AsyncEnumerable.ToListAsync(stream, Ct);
        Assert.Equal(first, second);
    }

    private async Task<long> OpenCursorsAsync()
    {
        var admin = new MongoClient(Fixture.ConnectionString).GetDatabase("admin");
        var status = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("serverStatus", 1), cancellationToken: Ct);
        return status["metrics"]["cursor"]["open"]["total"].ToInt64();
    }
}
