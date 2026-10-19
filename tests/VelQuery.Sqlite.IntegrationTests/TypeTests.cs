using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace VelQuery.Sqlite.IntegrationTests;

public enum LiteColor
{
    Red = 1,
    Green = 2,
}

public sealed record LiteTypeRow(long Id, short? I2, int? I4, long? I8, decimal? Num, bool? Flag, string? Txt, Guid? Uid,
    DateTime? Ts, DateTimeOffset? Tstz, DateOnly? D, TimeOnly? Tm, TimeSpan? Span, byte[]? Bytes, double? Dbl, float? Flt,
    LiteColor? Color, byte? U8, char? Ch);

public sealed record LiteStrictTypeRow(long Id, short I2, int I4, long I8, decimal Num, bool Flag, string Txt, Guid Uid,
    DateTime Ts, DateTimeOffset Tstz, DateOnly D, TimeOnly Tm, TimeSpan Span, byte[] Bytes, double Dbl, float Flt,
    LiteColor Color, byte U8, char Ch);

[VelQuerySql("sqlite.types", "SELECT id, i2, i4, i8, num, flag, txt, uid, ts, tstz, d, tm, span, bytes, dbl, flt, color, u8, ch FROM types ORDER BY id")]
public static partial class LiteTypes
{
    public static partial Task<IReadOnlyList<LiteTypeRow>> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("sqlite.types-strict", "SELECT id, i2, i4, i8, num, flag, txt, uid, ts, tstz, d, tm, span, bytes, dbl, flt, color, u8, ch FROM types WHERE id = 1")]
public static partial class LiteStrictTypes
{
    public static partial Task<LiteStrictTypeRow> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

/// <summary>
/// Type round-trips through <see cref="Providers.ProviderCapabilities.NativeConversions"/>: SQLite stores most CLR types as
/// TEXT/INTEGER/REAL and Microsoft.Data.Sqlite's typed getters convert them back. Also pins down the coercions SQLite
/// applies that VelQuery's lossless converter would have rejected, so a driver change shows up here.
/// </summary>
public sealed class TypeTests(SqliteFixture fixture) : IntegrationTest(fixture)
{
    private static readonly Guid Uid = Guid.Parse("a3c1d865-9684-06a4-f000-000000000001");
    private static readonly DateTime Ts = new(2026, 10, 11, 8, 30, 15, 123, 456);
    private static readonly DateTimeOffset Tstz = new(2026, 10, 11, 8, 30, 15, 123, 456, TimeSpan.FromHours(5.5));
    private static readonly string LongText = "💡 unicode ✓ " + new string('é', 5000);

    private static void AssertRow1(LiteTypeRow r)
    {
        Assert.Equal((short)32767, r.I2);
        Assert.Equal(int.MaxValue, r.I4);
        Assert.Equal(long.MaxValue, r.I8);
        Assert.Equal(12345678901234567890.12345678m, r.Num);
        Assert.True(r.Flag);
        Assert.Equal(LongText, r.Txt);
        Assert.Equal(Uid, r.Uid);
        Assert.Equal(Ts, r.Ts);
        Assert.Equal(Tstz, r.Tstz);
        Assert.Equal(Tstz.Offset, r.Tstz!.Value.Offset);
        Assert.Equal(new DateOnly(2026, 10, 11), r.D);
        Assert.Equal(new TimeOnly(13, 45, 7, 500), r.Tm);
        Assert.Equal(new TimeSpan(1, 6, 30, 0), r.Span);
        Assert.Equal(new byte[] { 0x00, 0xff, 0x10 }, r.Bytes);
        Assert.Equal(1.5, r.Dbl);
        Assert.Equal(0.25f, r.Flt);
        Assert.Equal(LiteColor.Green, r.Color);
        Assert.Equal((byte)200, r.U8);
        Assert.Equal('x', r.Ch);
    }

    [Fact]
    public async Task Stored_representations_map_through_native_conversions_and_nulls_stay_null()
    {
        await using var session = Session(new VelQueryOptions());

        var rows = await LiteTypes.ExecuteAsync(session, Ct);

        AssertRow1(rows[0]);
        Assert.Equal(new LiteTypeRow(2, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null), rows[1]);
    }

    [Fact]
    public async Task Non_nullable_members_use_the_same_conversions()
    {
        await using var session = Session(new VelQueryOptions());

        var r = await LiteStrictTypes.ExecuteAsync(session, Ct);

        AssertRow1(new LiteTypeRow(r.Id, r.I2, r.I4, r.I8, r.Num, r.Flag, r.Txt, r.Uid, r.Ts, r.Tstz, r.D, r.Tm, r.Span, r.Bytes, r.Dbl, r.Flt, r.Color, r.U8, r.Ch));
    }

    [Fact]
    public async Task Values_bound_by_velquery_round_trip_through_a_table()
    {
        var table = await CreateTableAsync("id INTEGER PRIMARY KEY, i2 INTEGER, i4 INTEGER, i8 INTEGER, num TEXT, flag INTEGER, txt TEXT, uid TEXT, ts TEXT, tstz TEXT, d TEXT, tm TEXT, span TEXT, bytes BLOB, dbl REAL, flt REAL, color INTEGER, u8 INTEGER, ch TEXT");
        await using var session = Session(new VelQueryOptions());
        var p = new QueryParameters()
            .Add("I2", (short)32767).Add("I4", int.MaxValue).Add("I8", long.MaxValue).Add("Num", 12345678901234567890.12345678m)
            .Add("Flag", true).Add("Txt", LongText).Add("Uid", Uid).Add("Ts", Ts).Add("Tstz", Tstz).Add("D", new DateOnly(2026, 10, 11))
            .Add("Tm", new TimeOnly(13, 45, 7, 500)).Add("Span", new TimeSpan(1, 6, 30, 0)).Add("Bytes", new byte[] { 0x00, 0xff, 0x10 })
            .Add("Dbl", 1.5).Add("Flt", 0.25f).Add("Color", LiteColor.Green).Add("U8", (byte)200).Add("Ch", 'x');
        await session.ExecuteAsync($"INSERT INTO {table} VALUES (1, @I2, @I4, @I8, @Num, @Flag, @Txt, @Uid, @Ts, @Tstz, @D, @Tm, @Span, @Bytes, @Dbl, @Flt, @Color, @U8, @Ch)", p, Ct);

        // The generated mapper for LiteTypeRow is registered, so runtime SQL uses it too (no reflection).
        var rows = await session.QueryAsync<LiteTypeRow>($"SELECT * FROM {table}", null, Ct);
        AssertRow1(rows[0]);

        // Bound values use the same text formats as the fixture's literals, so they compare equal in SQL.
        Assert.Equal(1L, await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table} t JOIN types s ON s.id = 1 AND s.ts = t.ts AND s.tstz = t.tstz AND s.uid = t.uid AND s.num = t.num AND s.d = t.d AND s.tm = t.tm", cancellationToken: Ct));
        Assert.Equal("text|text|text|integer|blob", await session.ExecuteScalarAsync<string>($"SELECT typeof(ts) || '|' || typeof(uid) || '|' || typeof(num) || '|' || typeof(flag) || '|' || typeof(bytes) FROM {table}", cancellationToken: Ct));
    }

    [Fact]
    public async Task Scalars_use_native_conversions()
    {
        await using var session = Session(new VelQueryOptions());

        Assert.Equal(Ts, await session.ExecuteScalarAsync<DateTime>("SELECT ts FROM types WHERE id = 1", cancellationToken: Ct));
        Assert.Equal(Tstz, await session.ExecuteScalarAsync<DateTimeOffset?>("SELECT tstz FROM types WHERE id = 1", cancellationToken: Ct));
        Assert.Null(await session.ExecuteScalarAsync<DateTimeOffset?>("SELECT tstz FROM types WHERE id = 2", cancellationToken: Ct));
        Assert.Equal(12345678901234567890.12345678m, await session.ExecuteScalarAsync<decimal>("SELECT num FROM types WHERE id = 1", cancellationToken: Ct));
        Assert.Equal(Uid, await session.ExecuteScalarAsync<Guid>("SELECT uid FROM types WHERE id = 1", cancellationToken: Ct));
        Assert.True(await session.ExecuteScalarAsync<bool>("SELECT flag FROM types WHERE id = 1", cancellationToken: Ct));
        Assert.Equal(LiteColor.Green, await session.ExecuteScalarAsync<LiteColor>("SELECT color FROM types WHERE id = 1", cancellationToken: Ct));
        Assert.Equal([new DateOnly(2026, 10, 11)], await session.QueryAsync<DateOnly>("SELECT d FROM types WHERE id = 1", null, Ct));

        // Nullable<enum> scalars use the lossless integral-to-enum rule (drivers cannot produce Nullable<enum> themselves).
        Assert.Equal(LiteColor.Green, await session.ExecuteScalarAsync<LiteColor?>("SELECT color FROM types WHERE id = 1", cancellationToken: Ct));
        Assert.Null(await session.ExecuteScalarAsync<LiteColor?>("SELECT color FROM types WHERE id = 2", cancellationToken: Ct));
    }

    [Fact]
    public async Task Driver_conversions_lose_information_that_velquery_documents()
    {
        await using var session = Session(new VelQueryOptions());

        // DateTime.Kind is not stored: a UTC value comes back Unspecified (same wall-clock time).
        var utc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var read = await session.ExecuteScalarAsync<DateTime>("SELECT @At", new QueryParameters().Add("At", utc), Ct);
        Assert.Equal(DateTimeKind.Unspecified, read.Kind);
        Assert.Equal(utc.Ticks, read.Ticks);

        // A text without an offset reads as offset zero.
        Assert.Equal(TimeSpan.Zero, (await session.ExecuteScalarAsync<DateTimeOffset>("SELECT '2026-10-11 08:30:15'", cancellationToken: Ct)).Offset);

        // SQLite's numeric coercions: REAL truncates into integers, non-numeric TEXT reads as 0/false, any integer is an enum.
        Assert.Equal(1, await session.ExecuteScalarAsync<int>("SELECT 1.9", cancellationToken: Ct));
        Assert.Equal(0L, await session.ExecuteScalarAsync<long>("SELECT 'abc'", cancellationToken: Ct));
        Assert.False(await session.ExecuteScalarAsync<bool>("SELECT 'true'", cancellationToken: Ct));
        Assert.Equal((LiteColor)99, await session.ExecuteScalarAsync<LiteColor>("SELECT 99", cancellationToken: Ct));

        // A decimal in a NUMERIC-affinity column is converted to REAL by SQLite on write (about 15 significant digits).
        var table = await CreateTableAsync("id INTEGER PRIMARY KEY, exact TEXT, lossy NUMERIC");
        var value = 12345678901234567890.12345678m;
        await session.ExecuteAsync($"INSERT INTO {table} VALUES (1, @V, @V)", new QueryParameters().Add("V", value), Ct);
        Assert.Equal(value, await session.ExecuteScalarAsync<decimal>($"SELECT exact FROM {table}", cancellationToken: Ct));
        Assert.NotEqual(value, await session.ExecuteScalarAsync<decimal>($"SELECT lossy FROM {table}", cancellationToken: Ct));

        // Text that does not parse is a driver FormatException, never a silent default.
        await Assert.ThrowsAsync<FormatException>(() => session.ExecuteScalarAsync<DateTime>("SELECT 'garbage'", cancellationToken: Ct));
        await Assert.ThrowsAsync<FormatException>(() => session.ExecuteScalarAsync<Guid>("SELECT 'nope'", cancellationToken: Ct));
        await Assert.ThrowsAsync<FormatException>(() => session.ExecuteScalarAsync<decimal>("SELECT 'abc'", cancellationToken: Ct));
    }

    [Fact]
    public async Task Strict_tables_reject_mismatched_values_at_write_time()
    {
        var table = "t_" + Guid.NewGuid().ToString("N");
        await using var session = Session();
        await session.ExecuteAsync($"CREATE TABLE {table} (id INTEGER PRIMARY KEY, qty INTEGER NOT NULL) STRICT", cancellationToken: Ct);

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() =>
            session.ExecuteAsync($"INSERT INTO {table} VALUES (1, @Qty)", new QueryParameters().Add("Qty", "abc"), Ct));

        Assert.Equal(19, ((SqliteException)ex.InnerException!).SqliteErrorCode); // SQLITE_CONSTRAINT (datatype)
    }

    public sealed record ReflectedDate(long Id, DateTime At);

    [Fact]
    public async Task Reflection_fallback_does_not_use_native_conversions()
    {
        // Known gap: the opt-in reflection mapper converts GetValue() with ValueConverter only, so TEXT dates fail.
        // Declare a [VelQuerySql] contract (or register a mapper) for row types with converted columns.
        await using var session = Session(new VelQueryOptions { AllowDynamicFallback = true });

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => session.QueryAsync<ReflectedDate>("SELECT id, ts AS at FROM types WHERE id = 1", null, Ct));

        Assert.Equal("At", ex.Column);
    }
}
