using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>Behavior that performance optimizations must preserve.</summary>
public sealed class PerformanceBehaviorTests
{
    private const string Sql = "select v from t";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sequential_access_is_used_only_when_the_mapper_supports_it()
    {
        var h = Harness.WithFallback();
        h.Script(Sql, FakeResultSet.Ints("v", 3));
        h.Script("select Id, Name, Email from p", new FakeResultSet(("Id", typeof(int)), ("Name", typeof(string)), ("Email", typeof(string))).Row(1, "a", null));
        await using var session = h.Session();

        await session.QueryAsync<int>(Sql, null, Ct);
        Assert.Equal(CommandBehavior.SequentialAccess, h.Db.Commands[^1].Behavior);

        // The reflection fallback reads members in declaration order, so it must not request sequential access.
        await session.QueryAsync<PersonClass>("select Id, Name, Email from p", null, Ct);
        Assert.Equal(CommandBehavior.Default, h.Db.Commands[^1].Behavior);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(1000)]
    public async Task Buffered_results_are_exact_size_and_in_reader_order(int count)
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", count));
        await using var session = h.Session();

        var rows = await session.QueryAsync(Descriptors.Scalar<int>(Sql), new NoArgs(), Ct);

        Assert.Equal(count, rows.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(i + 1, rows[i]);
        }

        var array = Assert.IsType<int[]>(rows);
        Assert.Equal(count, array.Length);
        if (count == 0)
        {
            Assert.Same(Array.Empty<int>(), rows);
        }
    }

    [Fact]
    public async Task Streaming_fast_path_yields_every_row_in_order_and_releases_resources()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 500));
        var seen = new List<int>();
        await using (var session = h.Session())
        {
            await foreach (var value in session.StreamAsync(Descriptors.Scalar<int>(Sql), new NoArgs(), Ct))
            {
                seen.Add(value);
            }
        }

        Assert.Equal(500, seen.Count);
        Assert.Equal(1, seen[0]);
        Assert.Equal(500, seen[^1]);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Mapper_failure_on_the_streaming_fast_path_releases_everything_and_cancels()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 100));
        var factory = new ThrowingAtRowFactory(37);
        var descriptor = new QueryDescriptor<NoArgs, int>("test.throwing", Sql, factory);
        await using (var session = h.Session())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in session.StreamAsync(descriptor, new NoArgs(), Ct))
                {
                }
            });
        }

        Assert.Equal(37, factory.Mapped);
        Assert.True(h.Db.Commands[0].CancelCount > 0);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Typed_scalar_mapper_honors_strict_nulls()
    {
        var h = new Harness();
        h.Script(Sql, new FakeResultSet(("v", typeof(int))).Row(1).Row(DBNull.Value));
        await using var session = h.Session();

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => session.QueryAsync<int>(Sql, null, Ct));
        Assert.Equal("v", ex.Column);

        var nullable = await session.QueryAsync<int?>(Sql, null, Ct);
        Assert.Equal(1, nullable[0]);
        Assert.Null(nullable[1]);
    }
}
