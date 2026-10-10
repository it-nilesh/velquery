using System;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using VelQuery.Mapping;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>VQ-FR-021 and PRD §8.2: ordered multi-result consumption, shape errors, disposal and outputs.</summary>
public sealed class MultipleResultTests
{
    private const string Sql = "select ...; select ...; select ...";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Harness ThreeSets(out FakeScript script)
    {
        var h = Harness.WithFallback();
        script = h.Script(Sql,
            FakeResultSet.Column<int>("v", 3, 1, 2),
            FakeResultSet.Column<string>("s", "a", "b"),
            new FakeResultSet(("Id", typeof(int)), ("Name", typeof(string)), ("Email", typeof(string))).Row(1, "Ann", null));
        script.OutputValues["Total"] = 3;
        return h;
    }

    [Theory]
    [InlineData(ApiPath.Descriptor)]
    [InlineData(ApiPath.RuntimeSql)]
    public async Task Result_sets_are_consumed_in_order_and_outputs_appear_after_the_last(ApiPath path)
    {
        var h = ThreeSets(out _);
        await using (var s = h.Session())
        {
            var parameters = new QueryParameters();
            var total = parameters.AddOutput<int>("Total");
            await using var multi = path == ApiPath.Descriptor
                ? await s.QueryMultipleAsync(new QueryDescriptor<QueryParameters>("multi.test", Sql, static (c, p, a) => a.Bind(c, p)), parameters, Ct)
                : await s.Sql(Sql, parameters).WithQueryId("multi.test").QueryMultipleAsync(Ct);

            Assert.Equal("multi.test", multi.QueryId);
            Assert.Equal(0, multi.ResultIndex);
            Assert.Equal([3, 1, 2], await multi.ReadAsync<int>(Ct));
            Assert.Equal(1, multi.ResultIndex);
            Assert.True(multi.HasMoreResults);
            Assert.False(total.HasValue);

            Assert.Equal(["a", "b"], await multi.ReadAsync(ScalarRowMapperFactory<string>.Instance, Ct));
            Assert.False(total.HasValue);

            Assert.Equal(new PersonRecord(1, "Ann", null), await multi.ReadSingleAsync<PersonRecord>(Ct));
            Assert.False(multi.HasMoreResults);
            Assert.Equal(3, multi.ResultIndex);

            // Descriptor binders own their parameters; only the runtime path binds QueryParameters outputs.
            if (path == ApiPath.RuntimeSql)
            {
                Assert.Equal(3, total.Value);
            }

            // Reader released after the last result; the command and connection are held until disposal.
            Assert.Equal(1, Assert.Single(h.Db.Readers).DisposeCount);
            Assert.Equal(0, Assert.Single(h.Db.Connections).DisposeCount);

            var ex = await Assert.ThrowsAsync<VelQueryValidationException>(() => multi.ReadAsync<int>(Ct));
            Assert.Contains("result-shape", ex.Message, StringComparison.Ordinal);
            Assert.Equal("multi.test", ex.QueryId);
        }

        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertNotCancelled(h.Db);
    }

    [Fact]
    public async Task Disposing_before_consuming_all_results_cancels_and_releases()
    {
        var h = ThreeSets(out _);
        var parameters = new QueryParameters();
        var total = parameters.AddOutput<int>("Total");
        await using (var s = h.Session())
        {
            var multi = await s.QueryMultipleAsync(Sql, parameters, Ct);
            await multi.ReadAsync<int>(Ct);
            await multi.DisposeAsync();
            await multi.DisposeAsync();

            Assert.False(multi.HasMoreResults);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => multi.ReadAsync<int>(Ct));
        }

        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertCancelledBeforeReaderDispose(h.Db);
        Assert.False(total.HasValue);
    }

    [Fact]
    public async Task Disposing_without_reading_cancels_and_releases()
    {
        var h = ThreeSets(out _);
        await using (var s = h.Session())
        {
            await using var multi = await s.QueryMultipleAsync(Sql, null, Ct);
        }

        h.Db.AssertAllReleasedExactlyOnce();
        Operations.AssertCancelledBeforeReaderDispose(h.Db);
    }

    [Fact]
    public async Task Single_read_from_a_set_with_wrong_cardinality_throws_typed_errors()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 0), FakeResultSet.Ints("v", 2));
        await using var s = h.Session();
        await using var multi = await s.QueryMultipleAsync(Sql, null, Ct);

        await Assert.ThrowsAsync<VelQueryNoRowsException>(() => multi.ReadSingleAsync<int>(Ct));
        await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => multi.ReadSingleAsync<int>(Ct));
    }

    [Fact]
    public async Task Command_returning_no_result_sets_reports_a_shape_error()
    {
        var h = new Harness();
        h.Db.Script(Sql, new FakeScript());
        await using (var s = h.Session())
        {
            await using var multi = await s.QueryMultipleAsync(Sql, null, Ct);
            Assert.False(multi.HasMoreResults);
            await Assert.ThrowsAsync<VelQueryValidationException>(() => multi.ReadAsync<int>(Ct));
        }

        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Open_failures_release_everything()
    {
        var h = new Harness();
        h.Db.Script(Sql, new FakeScript { ExecuteFault = new FakeDbException("timeout", isTimeout: true) });
        await using var s = h.Session();

        await Assert.ThrowsAsync<VelQueryTimeoutException>(() => s.QueryMultipleAsync(Sql, null, Ct));
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Multi_reader_on_caller_connection_holds_the_connection_until_disposed()
    {
        var h = ThreeSets(out _);
        using var connection = h.Db.OpenCallerConnection();
        await using var s = h.DataSource.CreateSession(connection);

        var multi = await s.QueryMultipleAsync(Sql, null, Ct);
        await Assert.ThrowsAsync<VelQueryValidationException>(() => s.ExecuteAsync(Sql, null, Ct));
        await multi.DisposeAsync();

        await s.ExecuteAsync(Sql, null, Ct);
        Assert.Equal(0, connection.DisposeCount);
    }
}
