using System;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>VQ-FR-005, VQ-FR-016, VQ-FR-029: ExecuteScalar null/no-row semantics and lossless conversions only.</summary>
public sealed class ScalarTests
{
    private const string Sql = "select count(*) from t";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Harness WithCell(Type type, object? value, bool strict = true)
    {
        var h = new Harness(new VelQueryOptions { StrictMapping = strict });
        h.Script(Sql, new FakeResultSet(("c", type)).Row(value));
        return h;
    }

    private static Harness NoRows()
    {
        var h = new Harness();
        h.Script(Sql, new FakeResultSet(("c", typeof(int))));
        return h;
    }

    private static async Task<T> Scalar<T>(Harness h, ApiPath path)
    {
        await using var s = h.Session();
        return path == ApiPath.Descriptor
            ? await s.ExecuteScalarAsync(Descriptors.Scalar<T>(Sql, "scalar.test"), new NoArgs(), Ct)
            : await s.Sql(Sql).WithQueryId("scalar.test").ExecuteScalarAsync<T>(Ct);
    }

    [Theory]
    [InlineData(ApiPath.Descriptor)]
    [InlineData(ApiPath.RuntimeSql)]
    public async Task Value_is_returned(ApiPath path)
    {
        var h = WithCell(typeof(int), 5);
        Assert.Equal(5, await Scalar<int>(h, path));
        Assert.Equal("Scalar", Assert.Single(h.Db.Commands).ExecutedAs);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [InlineData(ApiPath.Descriptor)]
    [InlineData(ApiPath.RuntimeSql)]
    public async Task No_rows_into_non_nullable_throws_no_rows(ApiPath path)
    {
        var h = NoRows();
        var ex = await Assert.ThrowsAsync<VelQueryNoRowsException>(() => Scalar<int>(h, path));
        Assert.Equal("scalar.test", ex.QueryId);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Theory]
    [InlineData(ApiPath.Descriptor)]
    [InlineData(ApiPath.RuntimeSql)]
    public async Task No_rows_into_nullable_targets_returns_null(ApiPath path)
    {
        Assert.Null(await Scalar<int?>(NoRows(), path));
        Assert.Null(await Scalar<string>(NoRows(), path));
    }

    [Theory]
    [InlineData(ApiPath.Descriptor)]
    [InlineData(ApiPath.RuntimeSql)]
    public async Task DBNull_into_non_nullable_fails_in_strict_mode(ApiPath path)
    {
        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => Scalar<int>(WithCell(typeof(int), DBNull.Value), path));
        Assert.Equal(typeof(int), ex.TargetType);
        Assert.Equal("scalar.test", ex.QueryId);
    }

    [Theory]
    [InlineData(ApiPath.Descriptor)]
    [InlineData(ApiPath.RuntimeSql)]
    public async Task DBNull_into_nullable_returns_null_and_is_distinct_from_no_rows(ApiPath path)
    {
        Assert.Null(await Scalar<int?>(WithCell(typeof(int), DBNull.Value), path));
        Assert.Null(await Scalar<string>(WithCell(typeof(string), DBNull.Value), path));
    }

    [Fact]
    public async Task DBNull_into_non_nullable_returns_default_when_strict_mapping_is_off()
        => Assert.Equal(0, await Scalar<int>(WithCell(typeof(int), DBNull.Value, strict: false), ApiPath.RuntimeSql));

    [Theory]
    [InlineData(ApiPath.Descriptor)]
    [InlineData(ApiPath.RuntimeSql)]
    public async Task Integral_widening_is_allowed(ApiPath path)
    {
        Assert.Equal(5L, await Scalar<long>(WithCell(typeof(int), 5), path));
        Assert.Equal(5L, await Scalar<long?>(WithCell(typeof(int), 5), path));
        Assert.Equal(5m, await Scalar<decimal>(WithCell(typeof(int), 5), path));
        Assert.Equal(5d, await Scalar<double>(WithCell(typeof(int), 5), path));
        Assert.Equal(1.5d, await Scalar<double>(WithCell(typeof(float), 1.5f), path));
        Assert.Equal(Status.Disabled, await Scalar<Status>(WithCell(typeof(int), 2), path));
        Assert.Equal(Status.Active, await Scalar<Status?>(WithCell(typeof(byte), (byte)1), path));
    }

    [Fact]
    public async Task In_range_narrowing_is_allowed()
        => Assert.Equal(5, await Scalar<int>(WithCell(typeof(long), 5L), ApiPath.RuntimeSql));

    [Fact]
    public async Task Out_of_range_narrowing_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => Scalar<int>(WithCell(typeof(long), long.MaxValue), ApiPath.RuntimeSql));
        Assert.IsType<OverflowException>(ex.InnerException);
        Assert.DoesNotContain(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<Type, object> LossyCells => new()
    {
        { typeof(double), 1.5d },
        { typeof(decimal), 1.0m },
        { typeof(string), "5" },
        { typeof(long), 1L },
        { typeof(DateTime), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
    };

    [Theory]
    [MemberData(nameof(LossyCells))]
    public async Task Lossy_or_parsing_conversions_to_int_or_double_are_rejected(Type type, object value)
    {
        // long -> double is lossy for large values; everything else is either lossy or would require parsing.
        if (type == typeof(long))
        {
            await Assert.ThrowsAsync<VelQueryMappingException>(() => Scalar<double>(WithCell(type, value), ApiPath.RuntimeSql));
            return;
        }

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => Scalar<int>(WithCell(type, value), ApiPath.RuntimeSql));
        Assert.Contains(type.Name, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Int32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Decimal_to_double_is_rejected()
        => await Assert.ThrowsAsync<VelQueryMappingException>(() => Scalar<double>(WithCell(typeof(decimal), 1.25m), ApiPath.Descriptor));

    [Fact]
    public async Task First_cell_of_first_row_is_used_when_many_rows_are_returned()
    {
        var h = new Harness();
        h.Script(Sql, new FakeResultSet(("a", typeof(int)), ("b", typeof(int))).Row(1, 2).Row(3, 4));
        Assert.Equal(1, await Scalar<int>(h, ApiPath.RuntimeSql));
    }

    [Fact]
    public async Task Query_of_scalar_rows_converts_each_cell_losslessly()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Column<int>("v", 1, 2, null));
        await using var s = h.Session();

        Assert.Equal([1L, 2L, null], await s.QueryAsync<long?>(Sql, null, Ct));
        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => s.QueryAsync<long>(Sql, null, Ct));
        Assert.Equal("v", ex.Column);
        Assert.Equal(typeof(long), ex.TargetType);
        h.Db.AssertAllReleasedExactlyOnce();
    }
}
