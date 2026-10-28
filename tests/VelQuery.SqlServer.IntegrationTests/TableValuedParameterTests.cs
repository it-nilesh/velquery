using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

public sealed record OrderLine(string Sku, int Quantity, decimal Price, DateOnly? Day, LineStatus Status);

public enum LineStatus
{
    Open = 1,
    Shipped = 2,
}

public sealed record LineRow(string Sku, int Quantity, decimal Price, DateTime? Day, int Status);

/// <summary>A generated contract that takes a table-valued parameter (no explicit DbType needed).</summary>
[VelQuerySql("tvp.ids", "SELECT Id FROM @ids ORDER BY Id")]
public static partial class SelectIds
{
    public static partial Task<IReadOnlyList<int>> ExecuteAsync(IVelQuerySession session, SqlTableValue ids, CancellationToken cancellationToken = default);
}

/// <summary>SQL Server table-valued parameters through <see cref="SqlTableType{T}"/> (runtime SQL and generated contracts).</summary>
public sealed class TableValuedParameterTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    private static readonly SqlTableType<int> IdList = SqlTableType<int>.Create("dbo.VqIdList").Column("Id", id => id);

    private static readonly SqlTableType<OrderLine> Lines = SqlTableType<OrderLine>.Create("dbo.VqLines")
        .Column("Sku", l => l.Sku, maxLength: 20)
        .Column("Quantity", l => l.Quantity)
        .Column("Price", l => l.Price, precision: 10, scale: 2)
        .Column("Day", l => l.Day)
        .Column("Status", l => l.Status);

    private async Task EnsureTypesAsync()
    {
        await using var session = Session();
        await session.ExecuteAsync("IF TYPE_ID(N'dbo.VqIdList') IS NULL CREATE TYPE dbo.VqIdList AS TABLE (Id int NOT NULL)", cancellationToken: Ct);
        await session.ExecuteAsync(
            "IF TYPE_ID(N'dbo.VqLines') IS NULL CREATE TYPE dbo.VqLines AS TABLE (Sku nvarchar(20) NOT NULL, Quantity int NOT NULL, Price decimal(10,2) NOT NULL, Day date NULL, Status int NOT NULL)",
            cancellationToken: Ct);
    }

    [Fact]
    public async Task Runtime_sql_streams_rows_into_a_table_valued_parameter()
    {
        await EnsureTypesAsync();
        await using var session = Session();
        var ids = await session.QueryAsync<int>("SELECT Id FROM @ids ORDER BY Id", new QueryParameters().Add("ids", IdList.Rows([3, 1, 2])), Ct);
        Assert.Equal([1, 2, 3], ids);

        var joined = await session.QueryAsync<IdName>(
            "SELECT r.Id, r.Name FROM dbo.Rows100 r JOIN @ids i ON i.Id = r.Id ORDER BY r.Id",
            new QueryParameters().Add("ids", IdList.Rows(Enumerable.Range(5, 3))), Ct);
        Assert.Equal([5L, 6L, 7L], joined.Select(r => r.Id));
    }

    [Fact]
    public async Task An_empty_sequence_is_an_empty_table()
    {
        await EnsureTypesAsync();
        await using var session = Session();
        Assert.Empty(await session.QueryAsync<int>("SELECT Id FROM @ids", new QueryParameters().Add("ids", IdList.Rows([])), Ct));
        Assert.Equal(0, await session.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM @ids", new QueryParameters().Add("ids", IdList.Rows([])), Ct));
    }

    [Fact]
    public async Task Columns_of_every_kind_round_trip_including_nulls_dates_and_enums()
    {
        await EnsureTypesAsync();
        await using var session = Session();
        OrderLine[] lines =
        [
            new("A-1", 2, 9.99m, new DateOnly(2026, 10, 29), LineStatus.Shipped),
            new("B-2", 1, 120.50m, null, LineStatus.Open),
        ];

        var rows = await session.QueryAsync<LineRow>("SELECT Sku, Quantity, Price, Day, Status FROM @lines ORDER BY Sku",
            new QueryParameters().Add("lines", Lines.Rows(lines)), Ct);

        Assert.Equal(
        [
            new LineRow("A-1", 2, 9.99m, new DateTime(2026, 10, 29), 2),
            new LineRow("B-2", 1, 120.50m, null, 1),
        ], rows);
    }

    [Fact]
    public async Task A_generated_contract_binds_a_table_valued_parameter()
    {
        await EnsureTypesAsync();
        await using var session = Session();
        Assert.Equal([4, 8], await SelectIds.ExecuteAsync(session, IdList.Rows([8, 4]), Ct));
    }

    [Fact]
    public async Task The_source_sequence_is_enumerated_once()
    {
        await EnsureTypesAsync();
        var enumerations = 0;
        IEnumerable<int> Source()
        {
            enumerations++;
            yield return 1;
            yield return 2;
        }

        await using var session = Session();
        Assert.Equal([1, 2], await session.QueryAsync<int>("SELECT Id FROM @ids ORDER BY Id", new QueryParameters().Add("ids", IdList.Rows(Source())), Ct));
        Assert.Equal(1, enumerations);
    }

    [Fact]
    public async Task Lossy_and_undeclared_decimals_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => SqlTableType<decimal>.Create("dbo.X").Column("Amount", d => d));

        await EnsureTypesAsync();
        await using var session = Session();
        var lossy = Lines.Rows([new OrderLine("C-3", 1, 1.005m, null, LineStatus.Open)]);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => session.QueryAsync<LineRow>("SELECT Sku, Quantity, Price, Day, Status FROM @lines",
            new QueryParameters().Add("lines", lossy), Ct));
        var messages = string.Join(" | ", Chain(error).Select(e => e.Message));
        Assert.Contains("would be rounded", messages, StringComparison.Ordinal);
        Assert.DoesNotContain("1.005", messages, StringComparison.Ordinal); // values never appear in errors
    }

    private static IEnumerable<Exception> Chain(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }
}
