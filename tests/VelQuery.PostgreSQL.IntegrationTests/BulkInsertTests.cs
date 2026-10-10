using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using VelQuery.PostgreSQL.Bulk;
using Xunit;

namespace VelQuery.PostgreSQL.IntegrationTests;

public sealed record PgItem(int Id, string Name, decimal? Price, int Stock, DateTime CreatedUtc);

/// <summary>Binary COPY bulk insert: all-or-nothing, constraint-checked, session-transaction aware.</summary>
public sealed class BulkInsertTests(PostgreSqlFixture fixture) : IntegrationTest(fixture)
{
    private const string Columns = "id int PRIMARY KEY, name text NOT NULL, price numeric(18,2) NULL DEFAULT 9.99, stock int NOT NULL CHECK (stock >= 0), created_utc timestamptz NOT NULL";

    private static PostgreSqlBulkInsertPlan<PgItem> Plan(string table) => PostgreSqlBulkInsertPlan<PgItem>.Create(table)
        .Column("id", r => r.Id)
        .Column("name", r => r.Name)
        .Column("price", r => r.Price)
        .Column("stock", r => r.Stock)
        .Column("created_utc", r => r.CreatedUtc, NpgsqlDbType.TimestampTz);

    private static readonly DateTime Created = new(2026, 10, 11, 3, 0, 0, DateTimeKind.Utc);

    private static IEnumerable<PgItem> Items(int count, int failAt = -1)
        => Enumerable.Range(1, count).Select(i => new PgItem(i, "item-" + i, i % 5 == 0 ? null : i * 1.25m, i == failAt ? -1 : i, Created));

    private async Task<(long Count, long NullPrices, decimal Sum)> StatsAsync(string table)
    {
        await using var session = Session();
        return (await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct),
            await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table} WHERE price IS NULL", cancellationToken: Ct),
            await session.ExecuteScalarAsync<decimal?>($"SELECT sum(price) FROM {table}", cancellationToken: Ct) ?? 0m);
    }

    [Fact]
    public async Task Writes_every_row_with_exact_values_and_keeps_nulls()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();

        Assert.Equal(50_000, await session.BulkInsertAsync(Plan(table), Items(50_000), Ct));

        Assert.Equal((50_000L, 10_000L, Items(50_000).Sum(i => i.Price ?? 0m)), await StatsAsync(table));
        Assert.Equal(Created, await session.ExecuteScalarAsync<DateTime>($"SELECT max(created_utc) FROM {table}", cancellationToken: Ct));
    }

    [Fact]
    public async Task A_failing_row_aborts_the_whole_copy()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.BulkInsertAsync(Plan(table), Items(20_000, failAt: 15_000), Ct));

        Assert.Equal("23514", ((PostgresException)ex.InnerException!).SqlState);
        Assert.Equal("bulk:" + table, ex.QueryId);
        Assert.Equal(0L, (await StatsAsync(table)).Count);
    }

    [Fact]
    public async Task Duplicate_keys_surface_as_provider_errors()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();
        await session.BulkInsertAsync(Plan(table), Items(10), Ct);

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.BulkInsertAsync(Plan(table), Items(10), Ct));

        Assert.Equal("23505", ((PostgresException)ex.InnerException!).SqlState);
        Assert.Equal(10L, (await StatsAsync(table)).Count);
    }

    [Fact]
    public async Task Enlists_in_the_session_transaction()
    {
        var table = await CreateTableAsync(Columns);
        await using (var session = Session())
        {
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
            Assert.Equal(1_000, await session.BulkInsertAsync(Plan(table), Items(1_000), Ct));
            Assert.Equal(1_000L, await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct));
        }

        Assert.Equal(0L, (await StatsAsync(table)).Count);
    }

    [Fact]
    public async Task Streams_rows_from_an_async_source()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();

        Assert.Equal(30_000, await session.BulkInsertAsync(Plan(table), ProduceAsync(30_000, Ct), Ct));
        Assert.Equal(30_000L, (await StatsAsync(table)).Count);
    }

    private static async IAsyncEnumerable<PgItem> ProduceAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in Items(count))
        {
            if (item.Id % 5_000 == 0)
            {
                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    [Fact]
    public async Task Cancellation_aborts_the_copy_and_returns_the_connection()
    {
        var table = await CreateTableAsync(Columns);
        await using var dataSource = IsolatedPoolDataSource(maxPoolSize: 1, poolTag: "bulk-cancel");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using (var session = dataSource.CreateSession())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.BulkInsertAsync(Plan(table), CancelAfter(cts, 20_000), cts.Token));
        }

        Assert.Equal(0L, (await StatsAsync(table)).Count);
        await using var next = dataSource.CreateSession();
        Assert.Equal(1, await next.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
    }

    private static IEnumerable<PgItem> CancelAfter(CancellationTokenSource cts, int rows)
    {
        foreach (var item in Items(1_000_000))
        {
            if (item.Id == rows)
            {
                cts.Cancel();
            }

            yield return item;
        }
    }

    private enum Color
    {
        Red = 1,
    }

    [Theory]
    [InlineData("public.items; DROP TABLE victims")]
    [InlineData("a.b.c")]
    [InlineData("\"items\"")]
    public void Unsafe_names_and_enum_columns_are_rejected_when_the_plan_is_built(string table)
    {
        Assert.Throws<ArgumentException>(() => PostgreSqlBulkInsertPlan<PgItem>.Create(table));
        Assert.Throws<ArgumentException>(() => PostgreSqlBulkInsertPlan<PgItem>.Create("items").Column("c", _ => Color.Red));
    }
}
