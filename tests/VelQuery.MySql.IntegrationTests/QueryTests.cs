using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using MySqlConnector;
using Xunit;

namespace VelQuery.MySql.IntegrationTests;

/// <summary>Cardinality, scalar/execute semantics, parameters, injection, multi-result and procedures on MySQL/MariaDB.</summary>
public sealed class QueryTests(MySqlFixture fixture) : IntegrationTest(fixture)
{
    [Theory]
    [InlineData("rows100", 100)]
    [InlineData("rows100k", MySqlFixture.LargeRowCount)]
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
        // count(*) is BIGINT; narrowing to int is range-checked, not truncated.
        Assert.Equal(100, await session.ExecuteScalarAsync<int>("SELECT count(*) FROM rows100", cancellationToken: Ct));
        Assert.Null(await session.ExecuteScalarAsync<int?>("SELECT category FROM rows100 WHERE id = 10", cancellationToken: Ct));
        await Assert.ThrowsAsync<VelQueryNoRowsException>(() => session.ExecuteScalarAsync<int>("SELECT 1 FROM rows100 WHERE 1 = 0", cancellationToken: Ct));

        var table = await CreateTableAsync("id int PRIMARY KEY");
        Assert.Equal(3, await session.ExecuteAsync($"INSERT INTO {table} VALUES (1), (2), (3)", cancellationToken: Ct));
        Assert.Equal(2, await session.ExecuteAsync($"UPDATE {table} SET id = id + 10 WHERE id <= 2", cancellationToken: Ct));
    }

    [Fact]
    public async Task Affected_rows_report_found_rows_by_default_and_changed_rows_with_UseAffectedRows()
    {
        var table = await CreateTableAsync("id int PRIMARY KEY, v int NOT NULL");
        await using var session = Session();
        await session.ExecuteAsync($"INSERT INTO {table} VALUES (1, 0), (2, 0), (3, 1)", cancellationToken: Ct);
        var setZero = $"UPDATE {table} SET v = 0 WHERE id <= 3";

        // MySqlConnector's default (UseAffectedRows=false, CLIENT_FOUND_ROWS) reports rows matched by WHERE, like SQL Server and
        // PostgreSQL, even when the new value equals the old one.
        Assert.Equal(3, await session.ExecuteAsync(setZero, cancellationToken: Ct));

        await using var changed = Fixture.CreateDataSource(b => b.UseAffectedRows = true);
        await using var changedSession = changed.CreateSession();
        await session.ExecuteAsync($"UPDATE {table} SET v = 1 WHERE id = 3", cancellationToken: Ct);
        Assert.Equal(1, await changedSession.ExecuteAsync(setZero, cancellationToken: Ct));
        Assert.Equal(0, await changedSession.ExecuteAsync(setZero, cancellationToken: Ct));
    }

    [Theory]
    [InlineData("' OR 1=1 -- ")]
    [InlineData("'; DROP TABLE victims; -- ")]
    [InlineData("\\'; DROP TABLE victims; #")]
    [InlineData("`victims`; DROP TABLE victims")]
    [InlineData("@Name")]
    public async Task Parameter_values_are_data_not_sql(string payload)
    {
        await using var session = Session();

        var rows = await session.QueryAsync<IdName>("SELECT id, name FROM victims WHERE name = @Name", new QueryParameters().Add("Name", payload), Ct);

        Assert.Empty(rows);
        Assert.Equal(2L, await session.ExecuteScalarAsync<long>("SELECT count(*) FROM victims", cancellationToken: Ct));
        Assert.Equal(payload, await session.ExecuteScalarAsync<string>("SELECT @P", new QueryParameters().Add("P", payload), Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NO_BACKSLASH_ESCAPES_never_enables_injection_but_needs_the_matching_connection_option(bool noBackslashEscapesOption)
    {
        // MySqlConnector sends unprepared parameters as escaped literals. It always doubles quotes, so a server running
        // NO_BACKSLASH_ESCAPES cannot be injected; but unless the connection says NoBackslashEscapes=true it also doubles
        // backslashes, which that server mode then stores literally (data corruption, not injection).
        await using var dataSource = Fixture.CreateDataSource(b => b.NoBackslashEscapes = noBackslashEscapesOption);
        await using var session = dataSource.CreateSession();
        await using (await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            await session.ExecuteAsync("SET SESSION sql_mode = CONCAT(@@sql_mode, ',NO_BACKSLASH_ESCAPES')", cancellationToken: Ct);
            const string payload = "\\' OR 1=1 -- ";

            Assert.Empty(await session.QueryAsync<IdName>("SELECT id, name FROM victims WHERE name = @Name", new QueryParameters().Add("Name", payload), Ct));
            var echoed = await session.ExecuteScalarAsync<string>("SELECT @P", new QueryParameters().Add("P", payload), Ct);
            Assert.Equal(noBackslashEscapesOption ? payload : "\\" + payload, echoed);
        }
    }

    public sealed record TypeRow(int Id, sbyte? I1, short? I2, int? I4, long? I8, ulong? U8, decimal? Num, bool? Flag, ulong? Bits,
        string? Txt, Guid? Uid, byte[]? Ubin, DateTime? Dt, DateTime? Ts, DateOnly? D, TimeOnly? Tm, TimeSpan? TmLong, string? Js,
        byte[]? Blb, float? F, double? Dbl);

    private const string TypeColumns = "id, i1, i2, i4, i8, u8, num, flag, bits, txt, uid, ubin, dt, ts, d, tm, tm_long AS tmlong, js, blb, f, dbl";

    [Fact]
    public async Task MySql_types_map_without_loss_and_nulls_stay_null()
    {
        await using var session = Session();
        var rows = await session.QueryAsync<TypeRow>($"SELECT {TypeColumns} FROM types ORDER BY id", null, Ct);

        var r = rows[0];
        Assert.Equal((sbyte)-128, r.I1);
        Assert.Equal((short)32767, r.I2);
        Assert.Equal(int.MaxValue, r.I4);
        Assert.Equal(long.MaxValue, r.I8);
        Assert.Equal(ulong.MaxValue, r.U8);
        Assert.Equal(12345678901234567890.12345678m, r.Num);
        // TINYINT(1) is bool (TreatTinyAsBoolean=true by default); BIT(1) is UInt64.
        Assert.True(r.Flag);
        Assert.Equal(1UL, r.Bits);
        Assert.Equal("💡 unicode ✓ " + new string('é', 5000), r.Txt);
        // CHAR(36) is a Guid under the default GuidFormat (Char36).
        Assert.Equal(Guid.Parse("a3c1d865-9684-06a4-f000-000000000001"), r.Uid);
        Assert.Equal(Convert.FromHexString("a3c1d865968406a4f000000000000001"), r.Ubin);
        Assert.Equal(new DateTime(2026, 10, 11, 8, 30, 15, 123, 456), r.Dt);
        Assert.Equal(DateTimeKind.Unspecified, r.Dt!.Value.Kind);
        Assert.Equal(new DateTime(2026, 10, 11, 8, 30, 15, 123, 456), r.Ts);
        Assert.Equal(new DateOnly(2026, 10, 11), r.D);
        Assert.Equal(new TimeOnly(13, 45, 7, 500), r.Tm);
        // TIME spans -838:59:59..838:59:59, so TimeSpan (not TimeOnly) is the faithful target for durations.
        Assert.Equal(-TimeSpan.FromHours(800), r.TmLong);
        Assert.Equal("{\"a\": [1, 2]}", r.Js);
        Assert.Equal(new byte[] { 0x00, 0xff, 0x10 }, r.Blb);
        Assert.Equal(1.5f, r.F);
        Assert.Equal(2.25, r.Dbl);

        Assert.Equal(new TypeRow(2, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null),
            rows[1] with { Ubin = null, Blb = null });
    }

    public sealed record Conversions(TimeOnly? TmLong);

    [Fact]
    public async Task Lossy_or_ambiguous_conversions_are_rejected()
    {
        await using var session = Session();

        // BIT(1) is UInt64; there is no lossless UInt64 -> bool rule, so map BIT to an integer or use TINYINT(1)/BOOL.
        var bit = await Assert.ThrowsAsync<VelQueryMappingException>(() => session.ExecuteScalarAsync<bool>("SELECT bits FROM types WHERE id = 1", cancellationToken: Ct));
        Assert.Equal(typeof(bool), bit.TargetType);
        Assert.Equal((byte)1, await session.ExecuteScalarAsync<byte>("SELECT bits FROM types WHERE id = 1", cancellationToken: Ct));

        // DATETIME carries no offset; with the default DateTimeKind=Unspecified VelQuery does not guess one.
        await Assert.ThrowsAsync<VelQueryMappingException>(() => session.ExecuteScalarAsync<DateTimeOffset>("SELECT dt FROM types WHERE id = 1", cancellationToken: Ct));

        // A TIME outside 0..24h is not a time of day.
        await Assert.ThrowsAsync<VelQueryMappingException>(() => session.QueryAsync<Conversions>("SELECT tm_long AS tmlong FROM types WHERE id = 1", null, Ct));

        // BINARY(16) is not a Guid unless the connection chooses a byte order (GuidFormat).
        await Assert.ThrowsAsync<VelQueryMappingException>(() => session.ExecuteScalarAsync<Guid>("SELECT ubin FROM types WHERE id = 1", cancellationToken: Ct));
    }

    [Fact]
    public async Task Connection_options_select_utc_datetimes_and_binary_guids()
    {
        await using var dataSource = Fixture.CreateDataSource(b =>
        {
            b.DateTimeKind = MySqlDateTimeKind.Utc;
            b.GuidFormat = MySqlGuidFormat.Binary16;
        });
        await using var session = dataSource.CreateSession();

        Assert.Equal(new DateTimeOffset(2026, 10, 11, 8, 30, 15, 123, 456, TimeSpan.Zero),
            await session.ExecuteScalarAsync<DateTimeOffset>("SELECT dt FROM types WHERE id = 1", cancellationToken: Ct));
        Assert.Equal(Guid.Parse("a3c1d865-9684-06a4-f000-000000000001"), await session.ExecuteScalarAsync<Guid>("SELECT ubin FROM types WHERE id = 1", cancellationToken: Ct));
        // Binary16 turns CHAR(36) back into plain text.
        Assert.Equal("a3c1d865-9684-06a4-f000-000000000001", await session.ExecuteScalarAsync<string>("SELECT uid FROM types WHERE id = 1", cancellationToken: Ct));
    }

    [Fact]
    public async Task Decimal_beyond_dotnet_range_is_a_driver_error_unless_read_as_text()
    {
        await using var session = Session();

        // DECIMAL(38,4) holding 34 integer digits exceeds System.Decimal; MySqlConnector raises FormatException while reading.
        // It is not a DbException, so it passes through VelQuery unwrapped (as Npgsql's numeric overflow does).
        await Assert.ThrowsAsync<FormatException>(() => session.ExecuteScalarAsync<decimal>("SELECT big FROM types WHERE id = 1", cancellationToken: Ct));
        await Assert.ThrowsAsync<FormatException>(() => session.QueryAsync<decimal>("SELECT big FROM types WHERE id = 1", null, Ct));
        Assert.Equal("1234567890123456789012345678901234.5678", await session.ExecuteScalarAsync<string>("SELECT CAST(big AS CHAR) FROM types WHERE id = 1", cancellationToken: Ct));
    }

    public sealed record Echo(long L, decimal Num, string Uid, DateOnly D, TimeOnly T, ulong U, string? Missing, long Flag);

    [Fact]
    public async Task Parameters_round_trip_through_the_server()
    {
        await using var session = Session();
        var guid = Guid.NewGuid();

        // A selected bool parameter comes back as BIGINT (MySQL has no boolean type); store into TINYINT(1) to read a bool.
        var echoed = await session.QuerySingleAsync<Echo>(
            "SELECT @L AS l, @Num AS num, CAST(@Uid AS CHAR(40)) AS uid, @D AS d, @T AS t, @U AS u, @Missing AS missing, @Flag AS flag", new QueryParameters()
                .Add("L", long.MinValue).Add("Num", 1234567890.12345678m).Add("Uid", guid).Add("D", new DateOnly(2026, 10, 11))
                .Add("T", new TimeOnly(13, 45, 7, 500)).Add("U", ulong.MaxValue).Add<string?>("Missing", null, DbType.String).Add("Flag", true), Ct);

        Assert.Equal(new Echo(long.MinValue, 1234567890.12345678m, guid.ToString(), new DateOnly(2026, 10, 11), new TimeOnly(13, 45, 7, 500), ulong.MaxValue, null, 1), echoed);
    }

    [Fact]
    public async Task Temporal_parameters_are_stored_as_sent_and_DateTimeOffset_as_its_utc_instant()
    {
        var table = await CreateTableAsync("id int PRIMARY KEY, dt datetime(6) NOT NULL, d date NOT NULL, g char(36) NOT NULL, flag tinyint(1) NOT NULL");
        await using var session = Session();
        var local = new DateTime(2026, 10, 11, 8, 30, 15, 123, 456, DateTimeKind.Unspecified);
        var offset = new DateTimeOffset(2026, 10, 11, 8, 30, 0, TimeSpan.FromHours(5.5));
        var guid = Guid.NewGuid();

        await session.ExecuteAsync($"INSERT INTO {table} VALUES (1, @Dt, @D, @G, @Flag), (2, @At, @D, @G, @Flag)", new QueryParameters()
            .Add("Dt", local).Add("At", offset).Add("D", new DateOnly(2026, 10, 11)).Add("G", guid).Add("Flag", true), Ct);
        Assert.True(await session.ExecuteScalarAsync<bool>($"SELECT flag FROM {table} WHERE id = 1", cancellationToken: Ct));

        Assert.Equal(local, await session.ExecuteScalarAsync<DateTime>($"SELECT dt FROM {table} WHERE id = 1", cancellationToken: Ct));
        Assert.Equal(offset.UtcDateTime, await session.ExecuteScalarAsync<DateTime>($"SELECT dt FROM {table} WHERE id = 2", cancellationToken: Ct));
        Assert.Equal(guid, await session.ExecuteScalarAsync<Guid>($"SELECT g FROM {table} WHERE id = 1", cancellationToken: Ct));
        Assert.Equal(new DateOnly(2026, 10, 11), await session.ExecuteScalarAsync<DateOnly>($"SELECT d FROM {table} WHERE id = 1", cancellationToken: Ct));
    }

    [Fact]
    public async Task Native_MySqlDbType_is_applied_from_the_parameter_spec()
    {
        var parameter = new MySqlParameter { Value = "{\"k\": 1}" };
        MySqlProvider.Instance.ConfigureParameter(parameter, new VelQuery.Parameters.ParameterSpec("J", ProviderDbType: (int)MySqlDbType.JSON), parameter.Value);
        Assert.Equal(MySqlDbType.JSON, parameter.MySqlDbType);

        await using var session = Session();
        var parameters = new QueryParameters().Add(new VelQuery.Parameters.ParameterSpec("J", ProviderDbType: (int)MySqlDbType.JSON), "{\"k\": 1}");
        Assert.Equal(1L, await session.ExecuteScalarAsync<long>("SELECT CAST(JSON_EXTRACT(@J, '$.k') AS SIGNED)", parameters, Ct));
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
            Assert.Equal(MySqlFixture.LargeRowCount, await multi.ReadSingleAsync<long>(Ct));
        }

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.QueryAsync<int>("SELECT 1; SELECT * FROM no_such_table", null, Ct));
        Assert.Equal(MySqlErrorCode.NoSuchTable, ((MySqlException)ex.InnerException!).ErrorCode);
    }

    [Fact]
    public async Task User_variables_need_AllowUserVariables()
    {
        await using var session = Session();
        // Without AllowUserVariables MySqlConnector treats @x as a missing parameter and fails before sending anything.
        await Assert.ThrowsAnyAsync<Exception>(() => session.ExecuteScalarAsync<long>("SET @x = 41; SELECT @x + 1", cancellationToken: Ct));

        await using var dataSource = Fixture.CreateDataSource(b => b.AllowUserVariables = true);
        await using var withVariables = dataSource.CreateSession();
        Assert.Equal(42L, await withVariables.ExecuteScalarAsync<long>("SET @x = 41; SELECT @x + @One", new QueryParameters().Add("One", 1L), Ct));
    }

    [Fact]
    public async Task Procedures_run_with_inout_and_out_parameters_captured()
    {
        await using var session = Session();
        var parameters = new QueryParameters();
        var doubled = parameters.AddOutput<int>("v", DbType.Int32, direction: ParameterDirection.InputOutput, inputValue: 21);

        await session.Sql("double_it", parameters).AsStoredProcedure().ExecuteAsync(Ct);

        Assert.Equal(42, doubled.Value);

        var countParameters = new QueryParameters().Add("max_id", 7L);
        var total = countParameters.AddOutput<long>("total", DbType.Int64);
        await session.Sql("count_upto", countParameters).AsStoredProcedure().ExecuteAsync(Ct);
        Assert.Equal(7L, total.Value);

        var id = Random.Shared.Next(1_000, int.MaxValue);
        await session.Sql("insert_victim", new QueryParameters().Add("p_id", id).Add("p_name", "carol")).AsStoredProcedure().ExecuteAsync(Ct);
        Assert.Equal("carol", await session.ExecuteScalarAsync<string>("SELECT name FROM victims WHERE id = @Id", new QueryParameters().Add("Id", id), Ct));
        await session.ExecuteAsync("DELETE FROM victims WHERE id = @Id", new QueryParameters().Add("Id", id), Ct);
    }

    [Fact]
    public async Task Identifiers_are_quoted_with_backticks()
    {
        var provider = MySqlProvider.Instance;
        Assert.Equal("`order`", provider.QuoteIdentifier("order"));
        Assert.Equal("`a``b`", provider.QuoteIdentifier("a`b"));
        Assert.Throws<ArgumentException>(() => provider.QuoteIdentifier(new string('x', 65)));

        var table = await CreateTableAsync("`order` int PRIMARY KEY, `select` varchar(10) NOT NULL");
        await using var session = Session();
        await session.ExecuteAsync($"INSERT INTO {table} ({provider.QuoteIdentifier("order")}, {provider.QuoteIdentifier("select")}) VALUES (1, 'x')", cancellationToken: Ct);
        Assert.Equal("x", await session.ExecuteScalarAsync<string>($"SELECT `select` FROM {table} WHERE `order` = 1", cancellationToken: Ct));
    }
}
