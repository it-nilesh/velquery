using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace VelQuery.PostgreSQL.IntegrationTests;

/// <summary>Cardinality, scalar/execute semantics, parameters, injection, multi-result and procedures on PostgreSQL.</summary>
public sealed class QueryTests(PostgreSqlFixture fixture) : IntegrationTest(fixture)
{
    [Theory]
    [InlineData("rows100", 100)]
    [InlineData("rows100k", PostgreSqlFixture.LargeRowCount)]
    public async Task QueryAsync_returns_every_row_in_order(string table, int expected)
    {
        await using var session = Session();
        var rows = await session.QueryAsync<IdName>($"SELECT id, name FROM {table} ORDER BY id", null, Ct);

        Assert.Equal(expected, rows.Count);
        Assert.Equal(Enumerable.Range(1, expected).Select(i => (long)i), rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Cardinality_operations_follow_the_contract_table()
    {
        await using var session = Session();
        const string none = "SELECT id, name FROM rows100 WHERE id < 0";
        const string one = "SELECT id, name FROM rows100 WHERE id = 7";
        const string many = "SELECT id, name FROM rows100 WHERE id <= 2 ORDER BY id";

        Assert.Empty(await session.QueryAsync<IdName>(none, null, Ct));
        await Assert.ThrowsAsync<VelQueryNoRowsException>(() => session.QueryFirstAsync<IdName>(none, null, Ct));
        Assert.Null(await session.QueryFirstOrDefaultAsync<IdName>(none, null, Ct));
        Assert.Equal(1, (await session.QueryFirstAsync<IdName>(many, null, Ct)).Id);
        Assert.Equal(new IdName(7, "row-7"), await session.QuerySingleAsync<IdName>(one, null, Ct));
        await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => session.QuerySingleAsync<IdName>(many, null, Ct));
        await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => session.QuerySingleOrDefaultAsync<IdName>(many, null, Ct));
        Assert.Null(await session.QuerySingleOrDefaultAsync<IdName>(none, null, Ct));
    }

    [Fact]
    public async Task Scalar_and_execute_report_provider_values()
    {
        await using var session = Session();
        Assert.Equal(100L, await session.ExecuteScalarAsync<long>("SELECT count(*) FROM rows100", cancellationToken: Ct));
        // count(*) is bigint; narrowing to int is range-checked, not truncated.
        Assert.Equal(100, await session.ExecuteScalarAsync<int>("SELECT count(*) FROM rows100", cancellationToken: Ct));
        Assert.Null(await session.ExecuteScalarAsync<int?>("SELECT category FROM rows100 WHERE id = 10", cancellationToken: Ct));
        await Assert.ThrowsAsync<VelQueryNoRowsException>(() => session.ExecuteScalarAsync<int>("SELECT 1 WHERE false", cancellationToken: Ct));

        var table = await CreateTableAsync("id int PRIMARY KEY");
        Assert.Equal(3, await session.ExecuteAsync($"INSERT INTO {table} VALUES (1), (2), (3)", cancellationToken: Ct));
        Assert.Equal(2, await session.ExecuteAsync($"UPDATE {table} SET id = id + 10 WHERE id <= 2", cancellationToken: Ct));
    }

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("'; DROP TABLE victims; --")]
    [InlineData("$$; DROP TABLE victims; $$")]
    public async Task Parameter_values_are_data_not_sql(string payload)
    {
        await using var session = Session();

        var rows = await session.QueryAsync<IdName>("SELECT id, name FROM victims WHERE name = @Name", new QueryParameters().Add("Name", payload), Ct);

        Assert.Empty(rows);
        Assert.Equal(2L, await session.ExecuteScalarAsync<long>("SELECT count(*) FROM victims", cancellationToken: Ct));
    }

    public sealed record TypeRow(int Id, short? I2, int? I4, long? I8, decimal? Num, bool? Flag, string? Txt, Guid? Uid,
        DateTime? Ts, DateTimeOffset? Tstz, DateOnly? D, TimeOnly? Tm, byte[]? Bytes);

    [Fact]
    public async Task Postgres_types_map_without_loss_and_nulls_stay_null()
    {
        await using var session = Session();
        var rows = await session.QueryAsync<TypeRow>("SELECT id, i2, i4, i8, num, flag, txt, uid, ts, tstz, d, tm, bytes FROM types ORDER BY id", null, Ct);

        var r = rows[0];
        Assert.Equal((short)32767, r.I2);
        Assert.Equal(int.MaxValue, r.I4);
        Assert.Equal(long.MaxValue, r.I8);
        Assert.Equal(12345678901234567890.12345678m, r.Num);
        Assert.True(r.Flag);
        Assert.Equal("💡 unicode ✓ " + new string('é', 5000), r.Txt);
        Assert.Equal(Guid.Parse("a3c1d865-9684-06a4-f000-000000000001"), r.Uid);
        Assert.Equal(new DateTime(2026, 10, 11, 8, 30, 15, 123, 456), r.Ts);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 8, 30, 15, 123, 456, TimeSpan.FromHours(5.5)), r.Tstz);
        Assert.Equal(new DateOnly(2026, 10, 11), r.D);
        Assert.Equal(new TimeOnly(13, 45, 7, 500), r.Tm);
        Assert.Equal(new byte[] { 0x00, 0xff, 0x10 }, r.Bytes);

        Assert.Equal(new TypeRow(2, null, null, null, null, null, null, null, null, null, null, null, null), rows[1] with { Bytes = null });
    }

    public sealed record Echo(long L, decimal Num, Guid Uid, DateTimeOffset At, string? Missing);

    [Fact]
    public async Task Parameters_round_trip_through_the_server()
    {
        await using var session = Session();
        // A non-zero offset is sent as the same instant in UTC (timestamptz stores instants).
        var offset = new DateTimeOffset(2026, 10, 11, 8, 30, 0, TimeSpan.FromHours(5.5));
        var guid = Guid.NewGuid();

        var echoed = await session.QuerySingleAsync<Echo>(
            "SELECT @L AS l, @Num AS num, @Uid AS uid, @At AS at, @Missing AS missing", new QueryParameters()
                .Add("L", long.MinValue).Add("Num", 1234567890.12345678m).Add("Uid", guid).Add("At", offset).Add<string?>("Missing", null, DbType.String), Ct);

        Assert.Equal(new Echo(long.MinValue, 1234567890.12345678m, guid, offset, null), echoed);
    }

    public sealed record StrictCategory(long Id, int Category);

    [Fact]
    public async Task Strict_mapping_rejects_null_into_non_nullable_and_missing_columns()
    {
        await using var session = Session();

        var nullEx = await Assert.ThrowsAsync<VelQueryMappingException>(() =>
            session.QueryAsync<StrictCategory>("SELECT id, category FROM rows100 WHERE id = 10", null, Ct));
        var missing = await Assert.ThrowsAsync<VelQueryMappingException>(() => session.QueryAsync<IdName>("SELECT id FROM rows100", null, Ct));

        Assert.Equal("Category", nullEx.Column);
        Assert.Equal("Name", missing.Column);
    }

    [Fact]
    public async Task Multiple_result_sets_are_read_in_order_and_later_errors_surface()
    {
        await using var session = Session();
        await using (var multi = await session.QueryMultipleAsync(
            "SELECT id, name FROM rows100 WHERE id = 1; SELECT count(*) FROM rows100k", new QueryParameters(), Ct))
        {
            Assert.Equal(new IdName(1, "row-1"), await multi.ReadSingleAsync<IdName>(Ct));
            Assert.Equal(PostgreSqlFixture.LargeRowCount, await multi.ReadSingleAsync<long>(Ct));
        }

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.QueryAsync<int>("SELECT 1; SELECT 1 / 0", null, Ct));
        Assert.Equal("22012", ((PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task Procedures_run_with_call_and_inout_parameters_are_captured()
    {
        await using var session = Session();
        var parameters = new QueryParameters();
        var doubled = parameters.AddOutput<int>("v", DbType.Int32, direction: ParameterDirection.InputOutput, inputValue: 21);

        await session.Sql("double_it", parameters).AsStoredProcedure().ExecuteAsync(Ct);

        Assert.Equal(42, doubled.Value);

        var id = Random.Shared.Next(1_000, int.MaxValue);
        await session.Sql("insert_victim", new QueryParameters().Add("p_id", id).Add("p_name", "carol")).AsStoredProcedure().ExecuteAsync(Ct);
        Assert.Equal("carol", await session.ExecuteScalarAsync<string>("SELECT name FROM victims WHERE id = @Id", new QueryParameters().Add("Id", id), Ct));
        await session.ExecuteAsync("DELETE FROM victims WHERE id = @Id", new QueryParameters().Add("Id", id), Ct);
    }
}
