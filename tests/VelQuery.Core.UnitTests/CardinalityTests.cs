using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using Xunit;

namespace VelQuery.Core.UnitTests;

public enum ApiPath
{
    Descriptor,
    RuntimeSql,
}

/// <summary>PRD §8.1 contract semantics table (VQ-FR-002..006, VQ-FR-013) on both the descriptor and runtime SQL paths.</summary>
public sealed class CardinalityTests
{
    private const string Sql = "select v from t";
    private const string QueryId = "cardinality.test";
    private static readonly int[] Values = [42, 7, 19, 3, 88];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ApiPath, int> PathsAndCounts()
    {
        var data = new TheoryData<ApiPath, int>();
        foreach (var path in Enum.GetValues<ApiPath>())
        {
            foreach (var count in new[] { 0, 1, 2, 5 })
            {
                data.Add(path, count);
            }
        }

        return data;
    }

    private static Harness WithRows(int count)
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Column<int>("v", [.. Values.Take(count).Cast<object?>()]));
        return h;
    }

    private static QueryDescriptor<NoArgs, int> Descriptor => Descriptors.Scalar<int>(Sql, QueryId);

    private static Task<IReadOnlyList<int>> Query(VelQuerySession s, ApiPath path)
        => path == ApiPath.Descriptor ? s.QueryAsync(Descriptor, new NoArgs(), Ct) : s.Sql(Sql).WithQueryId(QueryId).QueryAsync<int>(Ct);

    private static Task<int> First(VelQuerySession s, ApiPath path)
        => path == ApiPath.Descriptor ? s.QueryFirstAsync(Descriptor, new NoArgs(), Ct) : s.Sql(Sql).WithQueryId(QueryId).QueryFirstAsync<int>(Ct);

    private static Task<int> FirstOrDefault(VelQuerySession s, ApiPath path)
        => path == ApiPath.Descriptor ? s.QueryFirstOrDefaultAsync(Descriptor, new NoArgs(), Ct) : s.Sql(Sql).WithQueryId(QueryId).QueryFirstOrDefaultAsync<int>(Ct);

    private static Task<int> Single(VelQuerySession s, ApiPath path)
        => path == ApiPath.Descriptor ? s.QuerySingleAsync(Descriptor, new NoArgs(), Ct) : s.Sql(Sql).WithQueryId(QueryId).QuerySingleAsync<int>(Ct);

    private static Task<int> SingleOrDefault(VelQuerySession s, ApiPath path)
        => path == ApiPath.Descriptor ? s.QuerySingleOrDefaultAsync(Descriptor, new NoArgs(), Ct) : s.Sql(Sql).WithQueryId(QueryId).QuerySingleOrDefaultAsync<int>(Ct);

    private static IAsyncEnumerable<int> Stream(VelQuerySession s, ApiPath path)
        => path == ApiPath.Descriptor ? s.StreamAsync(Descriptor, new NoArgs(), Ct) : s.Sql(Sql).WithQueryId(QueryId).StreamAsync<int>(Ct);

    [Theory]
    [MemberData(nameof(PathsAndCounts))]
    public async Task QueryAsync_returns_every_row_in_reader_order(ApiPath path, int count)
    {
        var h = WithRows(count);
        await using var s = h.Session();

        var rows = await Query(s, path);

        Assert.Equal(Values.Take(count), rows);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [MemberData(nameof(PathsAndCounts))]
    public async Task StreamAsync_yields_every_row_in_reader_order(ApiPath path, int count)
    {
        var h = WithRows(count);
        await using var s = h.Session();

        var rows = new List<int>();
        await foreach (var row in Stream(s, path))
        {
            rows.Add(row);
        }

        Assert.Equal(Values.Take(count), rows);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [MemberData(nameof(PathsAndCounts))]
    public async Task QueryFirstAsync_returns_first_row_or_throws_no_rows(ApiPath path, int count)
    {
        var h = WithRows(count);
        await using var s = h.Session();

        if (count == 0)
        {
            var ex = await Assert.ThrowsAsync<VelQueryNoRowsException>(() => First(s, path));
            Assert.Equal(QueryId, ex.QueryId);
            Assert.Equal(VelQueryErrorCategory.NoRows, ex.Category);
        }
        else
        {
            Assert.Equal(Values[0], await First(s, path));
            Assert.Equal(CommandBehavior.SingleRow, Assert.Single(h.Db.Commands).Behavior);
        }

        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [MemberData(nameof(PathsAndCounts))]
    public async Task QueryFirstOrDefaultAsync_returns_first_row_or_default(ApiPath path, int count)
    {
        var h = WithRows(count);
        await using var s = h.Session();

        Assert.Equal(count == 0 ? 0 : Values[0], await FirstOrDefault(s, path));
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [MemberData(nameof(PathsAndCounts))]
    public async Task QuerySingleAsync_requires_exactly_one_row(ApiPath path, int count)
    {
        var h = WithRows(count);
        await using var s = h.Session();

        switch (count)
        {
            case 0:
                Assert.Equal(QueryId, (await Assert.ThrowsAsync<VelQueryNoRowsException>(() => Single(s, path))).QueryId);
                break;
            case 1:
                Assert.Equal(Values[0], await Single(s, path));
                break;
            default:
                var ex = await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => Single(s, path));
                Assert.Equal(QueryId, ex.QueryId);
                Assert.Equal(VelQueryErrorCategory.TooManyRows, ex.Category);
                Assert.Equal(2, Assert.Single(h.Db.Readers).RowsReturned);
                break;
        }

        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [MemberData(nameof(PathsAndCounts))]
    public async Task QuerySingleOrDefaultAsync_returns_default_for_zero_and_throws_for_many(ApiPath path, int count)
    {
        var h = WithRows(count);
        await using var s = h.Session();

        if (count > 1)
        {
            await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => SingleOrDefault(s, path));
        }
        else
        {
            Assert.Equal(count == 0 ? 0 : Values[0], await SingleOrDefault(s, path));
        }

        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task FirstOrDefault_and_SingleOrDefault_return_null_for_reference_rows()
    {
        var h = new Harness();
        h.Script(Sql, new FakeResultSet(("Id", typeof(int)), ("Name", typeof(string)), ("Email", typeof(string))));
        await using var s = h.Session();
        var descriptor = Descriptors.Reflection<PersonRecord>(Sql);

        Assert.Null(await s.QueryFirstOrDefaultAsync(descriptor, new NoArgs(), Ct));
        Assert.Null(await s.QuerySingleOrDefaultAsync(descriptor, new NoArgs(), Ct));
    }

    [Fact]
    public async Task Single_on_two_rows_never_returns_the_unchecked_first_row_for_records()
    {
        var h = new Harness();
        h.Script(Sql, new FakeResultSet(("Id", typeof(int)), ("Name", typeof(string)), ("Email", typeof(string)))
            .Row(1, "a", null)
            .Row(2, "b", null));
        await using var s = h.Session();

        await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => s.QuerySingleAsync(Descriptors.Reflection<PersonRecord>(Sql), new NoArgs(), Ct));
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Runtime_sql_without_query_id_uses_a_stable_value_free_sql_identity()
    {
        var h = WithRows(0);
        await using var s = h.Session();

        var first = await Assert.ThrowsAsync<VelQueryNoRowsException>(() => s.QueryFirstAsync<int>(Sql, null, Ct));
        var second = await Assert.ThrowsAsync<VelQueryNoRowsException>(() => s.QuerySingleAsync<int>(Sql, null, Ct));

        Assert.StartsWith("sql:", first.QueryId, StringComparison.Ordinal);
        Assert.Equal(first.QueryId, second.QueryId);
        Assert.DoesNotContain(Sql, first.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ApiPath.Descriptor, 7)]
    [InlineData(ApiPath.Descriptor, 0)]
    [InlineData(ApiPath.Descriptor, -1)]
    [InlineData(ApiPath.RuntimeSql, 7)]
    [InlineData(ApiPath.RuntimeSql, 0)]
    [InlineData(ApiPath.RuntimeSql, -1)]
    public async Task ExecuteAsync_returns_the_provider_count_unchanged(ApiPath path, int affected)
    {
        var h = new Harness();
        h.Db.Script(Sql, new FakeScript { RecordsAffected = affected });
        await using var s = h.Session();

        var result = path == ApiPath.Descriptor
            ? await s.ExecuteAsync(new QueryDescriptor<NoArgs>("cmd", Sql), new NoArgs(), Ct)
            : await s.ExecuteAsync(Sql, null, Ct);

        Assert.Equal(affected, result);
        Assert.Equal("NonQuery", Assert.Single(h.Db.Commands).ExecutedAs);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Command_text_and_type_are_passed_through()
    {
        var h = new Harness();
        h.Db.Script("dbo.DoWork", new FakeScript { RecordsAffected = 1 });
        await using var s = h.Session();

        await s.Sql("dbo.DoWork").AsStoredProcedure().ExecuteAsync(Ct);
        await s.ExecuteAsync(new QueryDescriptor<NoArgs>("proc", "dbo.DoWork", commandType: CommandType.StoredProcedure), new NoArgs(), Ct);

        Assert.All(h.Db.Commands, c =>
        {
            Assert.Equal("dbo.DoWork", c.CommandText);
            Assert.Equal(CommandType.StoredProcedure, c.CommandType);
        });
    }
}
