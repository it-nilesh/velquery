using System;
using System.Data;
using System.Threading.Tasks;
using VelQuery.Parameters;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

/// <summary>VQ-FR-014, PRD §18.1: lossless round trips of every seeded type, as results and as parameters.</summary>
public sealed class TypeFidelityTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Seeded_row_maps_every_type_without_loss()
    {
        await using var session = Session();
        var row = await session.QuerySingleAsync(Descriptors.TypesById, 1, Ct);

        AssertSeeded(row);
    }

    [Fact]
    public async Task Seeded_row_maps_through_reflection_fallback_without_loss()
    {
        await using var session = Session();
        var row = await session.QuerySingleAsync<TypesRow>("SELECT * FROM dbo.Types WHERE Id = 1", cancellationToken: Ct);

        AssertSeeded(row);
    }

    [Fact]
    public async Task All_null_row_maps_to_nullable_members()
    {
        await using var session = Session();
        var row = await session.QuerySingleAsync(Descriptors.TypesById, 2, Ct);

        Assert.Equal(2, row.Id);
        Assert.Null(row.LongText);
        Assert.Null(row.ShortText);
        Assert.Null(row.AnsiText);
        Assert.Null(row.Amount);
        Assert.Null(row.Created);
        Assert.Null(row.CreatedOffset);
        Assert.Null(row.UniqueId);
        Assert.Null(row.Data);
        Assert.Null(row.Flag);
        Assert.Null(row.Tiny);
        Assert.Null(row.Small);
        Assert.Null(row.Big);
    }

    [Fact]
    public async Task Parameters_match_stored_values_exactly_on_the_server()
    {
        // Equality is evaluated by SQL Server, so any truncation or rounding while binding would fail the predicate.
        await using var session = Session();
        var p = new QueryParameters()
            .Add("LongText", SqlServerFixture.LongUnicode)
            .Add("Amount", SqlServerFixture.HighPrecisionDecimal)
            .Add("Created", SqlServerFixture.Precise)
            .Add("CreatedOffset", SqlServerFixture.PreciseOffset)
            .Add("UniqueId", SqlServerFixture.KnownGuid)
            .Add("Data", SqlServerFixture.LargeBinary)
            .Add("Flag", true)
            .Add("Tiny", (byte)255)
            .Add("Small", short.MinValue)
            .Add("Big", long.MinValue);

        var id = await session.QuerySingleAsync<int>("""
            SELECT Id FROM dbo.Types
            WHERE LongText = @LongText AND Amount = @Amount AND Created = @Created
              AND CreatedOffset = @CreatedOffset AND DATEPART(TZOFFSET, CreatedOffset) = DATEPART(TZOFFSET, @CreatedOffset)
              AND UniqueId = @UniqueId AND Data = @Data AND Flag = @Flag
              AND Tiny = @Tiny AND Small = @Small AND Big = @Big
            """, p, Ct);
        Assert.Equal(1, id);
    }

    [Fact]
    public async Task Long_unicode_parameter_is_not_truncated()
    {
        await using var session = Session();
        var value = SqlServerFixture.LongUnicode;
        Assert.True(value.Length > 4000);

        var length = await session.ExecuteScalarAsync<long>(
            "SELECT CAST(DATALENGTH(@Value) AS bigint)", new QueryParameters().Add("Value", value), Ct);
        Assert.Equal(value.Length * 2L, length);

        var echoed = await session.ExecuteScalarAsync<string>("SELECT @Value", new QueryParameters().Add("Value", value), Ct);
        Assert.Equal(value, echoed);
    }

    [Fact]
    public async Task Written_values_round_trip_through_insert_and_select()
    {
        var marker = "types-" + Guid.NewGuid().ToString("N");
        await using var session = Session();

        // Use a temp table on a pinned connection (session transaction) so the round trip is isolated.
        await using var tx = await session.BeginTransactionAsync(cancellationToken: Ct);
        await session.ExecuteAsync("""
            CREATE TABLE #T (LongText nvarchar(max), Amount decimal(38,18), Created datetime2(7), CreatedOffset datetimeoffset(7),
                             UniqueId uniqueidentifier, Data varbinary(max), Flag bit, Tiny tinyint, Small smallint, Big bigint,
                             Ansi varchar(50), Tm time(7), Dt date, Dbl float, Marker nvarchar(100))
            """, cancellationToken: Ct);

        var offset = new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.FromHours(-9.5)).AddTicks(1);
        var p = new QueryParameters()
            .Add("LongText", SqlServerFixture.LongUnicode)
            .Add("Amount", -0.000000000000000001m)
            .Add("Created", DateTime.MaxValue)
            .Add("CreatedOffset", offset)
            .Add("UniqueId", Guid.Empty)
            .Add("Data", Array.Empty<byte>())
            .Add("Flag", false)
            .Add("Tiny", byte.MinValue)
            .Add("Small", short.MaxValue)
            .Add("Big", long.MaxValue)
            .Add("Ansi", "plain ascii", DbType.AnsiString)
            .Add("Tm", new TimeSpan(0, 23, 59, 59, 999).Add(TimeSpan.FromTicks(9999)))
            .Add("Dt", new DateOnly(1, 1, 1))
            .Add("Dbl", Math.PI)
            .Add("Marker", marker);
        Assert.Equal(1, await session.ExecuteAsync(
            "INSERT #T VALUES (@LongText, @Amount, @Created, @CreatedOffset, @UniqueId, @Data, @Flag, @Tiny, @Small, @Big, @Ansi, @Tm, @Dt, @Dbl, @Marker)", p, Ct));

        var row = await session.QuerySingleAsync<WrittenRow>("SELECT * FROM #T", cancellationToken: Ct);
        Assert.Equal(SqlServerFixture.LongUnicode, row.LongText);
        Assert.Equal(-0.000000000000000001m, row.Amount);
        Assert.Equal(DateTime.MaxValue, row.Created);
        Assert.Equal(offset, row.CreatedOffset);
        Assert.Equal(offset.Offset, row.CreatedOffset.Offset);
        Assert.Equal(Guid.Empty, row.UniqueId);
        Assert.Empty(row.Data);
        Assert.False(row.Flag);
        Assert.Equal(byte.MinValue, row.Tiny);
        Assert.Equal(short.MaxValue, row.Small);
        Assert.Equal(long.MaxValue, row.Big);
        Assert.Equal("plain ascii", row.Ansi);
        Assert.Equal(new TimeSpan(0, 23, 59, 59, 999).Add(TimeSpan.FromTicks(9999)), row.Tm);
        Assert.Equal(new DateTime(1, 1, 1), row.Dt);
        Assert.Equal(Math.PI, row.Dbl);
        Assert.Equal(marker, row.Marker);
    }

    [Fact]
    public async Task Null_parameters_are_bound_as_typed_db_null()
    {
        await using var session = Session();
        var p = new QueryParameters()
            .Add<string?>("S", null)
            .Add<int?>("I", null)
            .Add<byte[]?>("B", null)
            .Add<decimal?>("D", null);
        var result = await session.QuerySingleAsync<NullProbe>(
            "SELECT CASE WHEN @S IS NULL THEN 1 ELSE 0 END AS S, CASE WHEN @I IS NULL THEN 1 ELSE 0 END AS I, " +
            "CASE WHEN @B IS NULL THEN 1 ELSE 0 END AS B, CASE WHEN @D IS NULL THEN 1 ELSE 0 END AS D",
            p, Ct);
        Assert.Equal(new NullProbe(1, 1, 1, 1), result);

        // A typed NULL can be written and read back as NULL.
        Assert.Equal(2, await session.ExecuteScalarAsync<int>(
            "SELECT Id FROM dbo.Types WHERE Amount IS NULL AND @D IS NULL", new QueryParameters().Add<decimal?>("D", null), Ct));
    }

    [Fact]
    public async Task Decimal_with_explicit_precision_and_scale_is_bound_as_declared()
    {
        await using var session = Session();
        var p = new QueryParameters().Add(new ParameterSpec("Amount", DbType.Decimal, Precision: 38, Scale: 18), SqlServerFixture.HighPrecisionDecimal);
        var echoed = await session.ExecuteScalarAsync<decimal>("SELECT @Amount", p, Ct);
        Assert.Equal(SqlServerFixture.HighPrecisionDecimal, echoed);

        var type = await session.ExecuteScalarAsync<string>(
            "SELECT CONCAT(CAST(SQL_VARIANT_PROPERTY(CAST(@Amount AS sql_variant), 'Precision') AS int), ',', CAST(SQL_VARIANT_PROPERTY(CAST(@Amount AS sql_variant), 'Scale') AS int))",
            new QueryParameters().Add(new ParameterSpec("Amount", DbType.Decimal, Precision: 38, Scale: 18), 1.5m), Ct);
        Assert.Equal("38,18", type);
    }

    private static void AssertSeeded(TypesRow row)
    {
        Assert.Equal(1, row.Id);
        Assert.Equal(SqlServerFixture.LongUnicode, row.LongText);
        Assert.Equal(SqlServerFixture.LongUnicode.Length, row.LongText!.Length);
        Assert.Equal("short", row.ShortText);
        Assert.Equal("ansi", row.AnsiText);
        Assert.Equal(SqlServerFixture.HighPrecisionDecimal, row.Amount);
        Assert.Equal(SqlServerFixture.Precise, row.Created);
        Assert.Equal(SqlServerFixture.Precise.Ticks, row.Created!.Value.Ticks);
        Assert.Equal(SqlServerFixture.PreciseOffset, row.CreatedOffset);
        Assert.Equal(TimeSpan.FromMinutes(330), row.CreatedOffset!.Value.Offset);
        Assert.Equal(SqlServerFixture.PreciseOffset.Ticks, row.CreatedOffset.Value.Ticks);
        Assert.Equal(SqlServerFixture.KnownGuid, row.UniqueId);
        Assert.Equal(SqlServerFixture.LargeBinary, row.Data);
        Assert.True(row.Flag);
        Assert.Equal((byte)255, row.Tiny);
        Assert.Equal(short.MinValue, row.Small);
        Assert.Equal(long.MinValue, row.Big);
    }

    public sealed record NullProbe(int S, int I, int B, int D);

    public sealed record WrittenRow(
        string LongText, decimal Amount, DateTime Created, DateTimeOffset CreatedOffset, Guid UniqueId, byte[] Data, bool Flag,
        byte Tiny, short Small, long Big, string Ansi, TimeSpan Tm, DateTime Dt, double Dbl, string Marker);
}
