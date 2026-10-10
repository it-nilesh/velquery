using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>VQ-FR-007, VQ-FR-008, VQ-FR-023 and PRD §9.4 parameter binding rules.</summary>
public sealed class QueryParametersTests
{
    private const string Sql = "exec something";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IReadOnlyList<ParameterSnapshot>> BindAsync(QueryParameters parameters)
    {
        var h = new Harness();
        h.Db.Script(Sql, new FakeScript { RecordsAffected = 0 });
        await using var s = h.Session();
        await s.ExecuteAsync(Sql, parameters, Ct);
        return Assert.Single(h.Db.Commands).BoundParameters;
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("@Id")]
    [InlineData(":Id")]
    [InlineData("$Id")]
    [InlineData("?Id")]
    public async Task Names_are_normalized_and_prefixed_by_the_provider(string name)
    {
        var p = Assert.Single(await BindAsync(new QueryParameters().Add(name, 5)));
        Assert.Equal("@Id", p.Name);
        Assert.Equal(5, p.Value);
        Assert.Equal(ParameterDirection.Input, p.Direction);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("@")]
    [InlineData("@@Id")]
    [InlineData("1abc")]
    [InlineData("a b")]
    [InlineData("a-b")]
    [InlineData("Id;DROP")]
    [InlineData("Id'")]
    public void Invalid_names_are_rejected(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => new QueryParameters().Add(name, 1));
        Assert.ThrowsAny<ArgumentException>(() => new QueryParameters().AddOutput<int>(name));
    }

    [Fact]
    public void Null_name_is_rejected()
        => Assert.Throws<ArgumentNullException>(() => new QueryParameters().Add<int>(null!, 1));

    [Fact]
    public void Duplicate_names_are_rejected_case_insensitively_and_across_prefixes()
    {
        var parameters = new QueryParameters().Add("Id", 1);
        Assert.Throws<ArgumentException>(() => parameters.Add("@id", 2));
        Assert.Throws<ArgumentException>(() => parameters.AddOutput<int>(":ID"));
        Assert.Equal(1, parameters.Count);
    }

    [Fact]
    public async Task DbType_is_inferred_from_the_clr_type()
    {
        var bound = await BindAsync(new QueryParameters()
            .Add("i", 1)
            .Add("l", 1L)
            .Add("s", "x")
            .Add("b", true)
            .Add("d", 1.5m)
            .Add("f", 1.5d)
            .Add("g", Guid.Empty)
            .Add("dt", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .Add("dto", DateTimeOffset.UnixEpoch)
            .Add("date", new DateOnly(2024, 1, 1))
            .Add("bin", new byte[] { 1 })
            .Add("e", Status.Active)
            .Add<object>("boxed", 5L)
            .Add("ni", (int?)7));

        Assert.Equal(
            [DbType.Int32, DbType.Int64, DbType.String, DbType.Boolean, DbType.Decimal, DbType.Double, DbType.Guid, DbType.DateTime2,
             DbType.DateTimeOffset, DbType.Date, DbType.Binary, DbType.Int32, DbType.Int64, DbType.Int32],
            bound.ConvertAll(p => p.DbType));
        Assert.All(bound, p => Assert.True(p.DbTypeExplicit));
    }

    [Fact]
    public async Task Explicit_metadata_overrides_inference()
    {
        var p = Assert.Single(await BindAsync(new QueryParameters().Add("Price", 1.5m, DbType.Currency, size: 8, precision: 19, scale: 4)));
        Assert.Equal((DbType.Currency, 8, (byte)19, (byte)4), (p.DbType, p.Size, p.Precision, p.Scale));

        var a = Assert.Single(await BindAsync(new QueryParameters().Add("Code", "abc", DbType.AnsiStringFixedLength, size: 3)));
        Assert.Equal((DbType.AnsiStringFixedLength, 3), (a.DbType, a.Size));
    }

    [Fact]
    public async Task Null_values_bind_as_DBNull_and_keep_their_type_metadata()
    {
        var bound = await BindAsync(new QueryParameters()
            .Add<string?>("s", null, size: 50)
            .Add<int?>("i", null)
            .Add<object?>("o", null, DbType.Guid));

        Assert.All(bound, p => Assert.Same(DBNull.Value, p.Value));
        Assert.Equal((DbType.String, 50), (bound[0].DbType, bound[0].Size));
        Assert.Equal(DbType.Int32, bound[1].DbType);
        Assert.Equal(DbType.Guid, bound[2].DbType);
    }

    [Fact]
    public async Task Unknown_types_leave_DbType_to_the_driver()
    {
        var p = Assert.Single(await BindAsync(new QueryParameters().Add<object?>("o", null)));
        Assert.False(p.DbTypeExplicit);
    }

    [Fact]
    public async Task Provider_configures_every_parameter()
    {
        var h = new Harness();
        h.Db.Script(Sql, new FakeScript());
        await using var s = h.Session();
        await s.ExecuteAsync(Sql, new QueryParameters().Add("a", 1).Add("b", 2), Ct);
        Assert.Equal(2, h.Provider.ConfigureCalls);
    }

    [Fact]
    public async Task FromObject_binds_public_properties_with_inferred_types()
    {
        var bound = await BindAsync(QueryParameters.FromObject(new { Id = 5L, Name = "x", Missing = (string?)null, When = (DateTime?)null }));

        Assert.Equal(["@Id", "@Name", "@Missing", "@When"], bound.ConvertAll(p => p.Name));
        Assert.Equal([DbType.Int64, DbType.String, DbType.String, DbType.DateTime2], bound.ConvertAll(p => p.DbType));
        Assert.Equal(5L, bound[0].Value);
        Assert.Same(DBNull.Value, bound[2].Value);
        Assert.Same(DBNull.Value, bound[3].Value);
    }

    [Fact]
    public void FromObject_rejects_null()
        => Assert.Throws<ArgumentNullException>(() => QueryParameters.FromObject<object>(null!));

    [Fact]
    public async Task Parameters_are_created_fresh_for_every_execution()
    {
        var h = new Harness();
        h.Db.Script(Sql, new FakeScript());
        await using var s = h.Session();
        var parameters = new QueryParameters().Add("Id", 1);

        await s.ExecuteAsync(Sql, parameters, Ct);
        await s.ExecuteAsync(Sql, parameters, Ct);

        Assert.All(h.Db.Commands, c => Assert.Single(c.BoundParameters));
        Assert.NotSame(h.Db.Commands[0].Parameters[0], h.Db.Commands[1].Parameters[0]);
    }

    [Fact]
    public async Task Output_and_input_output_values_are_captured_after_execute()
    {
        var h = new Harness();
        var script = h.Db.Script(Sql, new FakeScript { RecordsAffected = 1 });
        script.OutputValues["Total"] = 42;
        script.OutputValues["Counter"] = 6L;
        script.OutputValues["Ret"] = 0;
        script.OutputValues["Maybe"] = null;
        await using var s = h.Session();

        var parameters = new QueryParameters().Add("Id", 1);
        var total = parameters.AddOutput<int>("Total");
        var counter = parameters.AddOutput<long>("@Counter", direction: ParameterDirection.InputOutput, inputValue: 5L);
        var ret = parameters.AddOutput<int>("Ret", direction: ParameterDirection.ReturnValue);
        var maybe = parameters.AddOutput<int?>("Maybe");

        Assert.False(total.HasValue);
        Assert.Throws<InvalidOperationException>(() => total.Value);

        await s.Sql(Sql, parameters).AsStoredProcedure().ExecuteAsync(Ct);

        Assert.Equal(42, total.Value);
        Assert.Equal(6L, counter.Value);
        Assert.Equal(0, ret.Value);
        Assert.True(maybe.HasValue);
        Assert.Null(maybe.Value);

        var bound = Assert.Single(h.Db.Commands).BoundParameters;
        Assert.Equal(
            [ParameterDirection.Input, ParameterDirection.Output, ParameterDirection.InputOutput, ParameterDirection.ReturnValue, ParameterDirection.Output],
            bound.ConvertAll(p => p.Direction));
        Assert.Equal(5L, bound[2].Value);
        Assert.Same(DBNull.Value, bound[1].Value);
        Assert.Equal(DbType.Int32, bound[1].DbType);
    }

    [Fact]
    public void Output_direction_must_not_be_input()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new QueryParameters().AddOutput<int>("x", direction: ParameterDirection.Input));

    [Fact]
    public async Task Output_values_are_captured_after_scalar_and_buffered_query()
    {
        var h = new Harness();
        var script = h.Script(Sql, FakeResultSet.Ints("v", 2));
        script.OutputValues["Total"] = 9;
        await using var s = h.Session();

        var p1 = new QueryParameters();
        var o1 = p1.AddOutput<int>("Total");
        await s.ExecuteScalarAsync<int>(Sql, p1, Ct);
        Assert.Equal(9, o1.Value);

        var p2 = new QueryParameters();
        var o2 = p2.AddOutput<int>("Total");
        await s.QueryAsync<int>(Sql, p2, Ct);
        Assert.Equal(9, o2.Value);

        var p3 = new QueryParameters();
        var o3 = p3.AddOutput<int>("Total");
        await s.QueryFirstAsync<int>(Sql, p3, Ct);
        Assert.Equal(9, o3.Value);
    }

    [Fact]
    public async Task Stream_outputs_are_available_only_after_all_rows_are_consumed()
    {
        var h = new Harness();
        var script = h.Script(Sql, FakeResultSet.Ints("v", 3), FakeResultSet.Ints("v", 1));
        script.OutputValues["Total"] = 3;
        await using var s = h.Session();
        var parameters = new QueryParameters();
        var total = parameters.AddOutput<int>("Total");

        await foreach (var _ in s.StreamAsync<int>(Sql, parameters, Ct))
        {
            Assert.False(total.HasValue);
        }

        Assert.Equal(3, total.Value);
    }

    [Fact]
    public async Task Abandoned_stream_never_populates_outputs()
    {
        var h = new Harness();
        var script = h.Script(Sql, FakeResultSet.Ints("v", 3));
        script.OutputValues["Total"] = 3;
        await using var s = h.Session();
        var parameters = new QueryParameters();
        var total = parameters.AddOutput<int>("Total");

        await foreach (var _ in s.StreamAsync<int>(Sql, parameters, Ct))
        {
            break;
        }

        Assert.False(total.HasValue);
    }

    [Fact]
    public async Task Failed_command_never_populates_outputs()
    {
        var h = new Harness();
        var script = h.Db.Script(Sql, new FakeScript { ExecuteFault = new FakeDbException() });
        script.OutputValues["Total"] = 3;
        await using var s = h.Session();
        var parameters = new QueryParameters();
        var total = parameters.AddOutput<int>("Total");

        await Assert.ThrowsAsync<VelQueryProviderException>(() => s.ExecuteAsync(Sql, parameters, Ct));
        Assert.False(total.HasValue);
    }

    [Fact]
    public async Task Null_output_into_non_nullable_handle_fails_in_strict_mode()
    {
        var h = new Harness();
        var script = h.Db.Script(Sql, new FakeScript());
        script.OutputValues["Total"] = null;
        await using var s = h.Session();
        var parameters = new QueryParameters();
        parameters.AddOutput<int>("Total");

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => s.ExecuteAsync(Sql, parameters, Ct));
        Assert.Equal("Total", ex.Column);
        h.Db.AssertAllReleasedExactlyOnce();
    }
}

internal static class SnapshotListExtensions
{
    public static List<TOut> ConvertAll<TIn, TOut>(this IReadOnlyList<TIn> source, Func<TIn, TOut> map)
    {
        var list = new List<TOut>(source.Count);
        foreach (var item in source)
        {
            list.Add(map(item));
        }

        return list;
    }
}
