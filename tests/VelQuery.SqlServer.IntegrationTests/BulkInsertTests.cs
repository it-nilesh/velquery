using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using VelQuery.SqlServer.Bulk;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

public sealed record BulkItem(int Id, string Name, decimal? Price, int Stock);

/// <summary>SQL Server bulk insert: throughput path with INSERT-like defaults and the usual VelQuery guarantees.</summary>
public sealed class BulkInsertTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    private static SqlBulkInsertPlan<BulkItem> Plan(string table, SqlBulkInsertOptions? options = null)
        => SqlBulkInsertPlan<BulkItem>.Create(table, options)
            .Column("Id", r => r.Id)
            .Column("Name", r => r.Name)
            .Column("Price", r => r.Price)
            .Column("Stock", r => r.Stock);

    private static IEnumerable<BulkItem> Items(int count, int failAt = -1)
        => Enumerable.Range(1, count).Select(i => new BulkItem(i, "item-" + i, i % 5 == 0 ? null : i * 1.25m, i == failAt ? -1 : i));

    private async Task<string> CreateTableAsync()
    {
        var table = "dbo.Bulk_" + Guid.NewGuid().ToString("N");
        await using var session = Session();
        // Price has a default so KeepNulls (NULL stays NULL) is observable; Stock has a CHECK constraint.
        await session.ExecuteAsync(
            $"CREATE TABLE {table} (Id int NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL, Price decimal(18,2) NULL DEFAULT 9.99, Stock int NOT NULL CHECK (Stock >= 0))",
            cancellationToken: Ct);
        return table;
    }

    private async Task<(long Count, long NullPrices, decimal Sum)> StatsAsync(string table)
    {
        await using var session = Session();
        var count = await session.ExecuteScalarAsync<long>($"SELECT COUNT_BIG(*) FROM {table}", cancellationToken: Ct);
        var nulls = await session.ExecuteScalarAsync<long>($"SELECT COUNT_BIG(*) FROM {table} WHERE Price IS NULL", cancellationToken: Ct);
        var sum = await session.ExecuteScalarAsync<decimal?>($"SELECT SUM(Price) FROM {table}", cancellationToken: Ct) ?? 0m;
        return (count, nulls, sum);
    }

    [Fact]
    public async Task Writes_every_row_with_exact_values_and_keeps_nulls()
    {
        var table = await CreateTableAsync();
        await using var session = Session();

        var written = await session.BulkInsertAsync(Plan(table), Items(25_000), Ct);

        Assert.Equal(25_000, written);
        var expectedSum = Items(25_000).Sum(i => i.Price ?? 0m);
        Assert.Equal((25_000L, 5_000L, expectedSum), await StatsAsync(table));
    }

    [Fact]
    public async Task Default_load_is_atomic_so_a_failing_row_leaves_no_partial_data()
    {
        var table = await CreateTableAsync();
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() =>
            session.BulkInsertAsync(Plan(table, new SqlBulkInsertOptions { BatchSize = 1_000 }), Items(10_000, failAt: 7_500), Ct));

        Assert.IsType<SqlException>(ex.InnerException);
        Assert.Equal("bulk:" + table, ex.QueryId);
        Assert.Equal(0, (await StatsAsync(table)).Count);
    }

    [Fact]
    public async Task Non_atomic_load_commits_batches_before_the_failure()
    {
        var table = await CreateTableAsync();
        await using var session = Session();

        await Assert.ThrowsAsync<VelQueryProviderException>(() =>
            session.BulkInsertAsync(Plan(table, new SqlBulkInsertOptions { BatchSize = 1_000, Atomic = false }), Items(10_000, failAt: 7_500), Ct));

        Assert.Equal(7_000, (await StatsAsync(table)).Count);
    }

    [Fact]
    public async Task Check_constraints_are_enforced_by_default_and_can_be_relaxed()
    {
        var table = await CreateTableAsync();
        await using var session = Session();

        await Assert.ThrowsAsync<VelQueryProviderException>(() => session.BulkInsertAsync(Plan(table), Items(10, failAt: 3), Ct));

        var relaxed = Plan(table, new SqlBulkInsertOptions { CheckConstraints = false });
        Assert.Equal(10, await session.BulkInsertAsync(relaxed, Items(10, failAt: 3), Ct));
    }

    [Fact]
    public async Task Enlists_in_the_session_transaction_and_rolls_back_with_it()
    {
        var table = await CreateTableAsync();
        await using (var session = Session())
        {
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
            Assert.Equal(1_000, await session.BulkInsertAsync(Plan(table), Items(1_000), Ct));
            Assert.Equal(1_000, await session.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table}", cancellationToken: Ct));
        }

        Assert.Equal(0, (await StatsAsync(table)).Count);
    }

    [Fact]
    public async Task Streams_rows_from_an_async_source()
    {
        var table = await CreateTableAsync();
        await using var session = Session();

        var written = await session.BulkInsertAsync(Plan(table), ProduceAsync(50_000, Ct), Ct);

        Assert.Equal(50_000, written);
        Assert.Equal(50_000, (await StatsAsync(table)).Count);
    }

    private static async IAsyncEnumerable<BulkItem> ProduceAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in Items(count))
        {
            if (item.Id % 10_000 == 0)
            {
                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    [Fact]
    public async Task Duplicate_keys_surface_as_provider_errors_with_the_original_exception()
    {
        var table = await CreateTableAsync();
        await using var session = Session();
        await session.BulkInsertAsync(Plan(table), Items(10), Ct);

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.BulkInsertAsync(Plan(table), Items(10), Ct));

        Assert.Contains(((SqlException)ex.InnerException!).Number, new[] { 2627, 2601 });
        Assert.Equal(10, (await StatsAsync(table)).Count);
    }

    [Fact]
    public async Task Cancellation_stops_the_load_and_returns_the_connection_to_the_pool()
    {
        var table = await CreateTableAsync();
        await using var dataSource = IsolatedPoolDataSource(maxPoolSize: 1, poolTag: "bulk-cancel");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using (var session = dataSource.CreateSession())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.BulkInsertAsync(Plan(table), CancelAfter(cts, 20_000), cts.Token));
        }

        Assert.Equal(0, (await StatsAsync(table)).Count);
        await using var next = dataSource.CreateSession();
        Assert.Equal(1, await next.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
    }

    private static IEnumerable<BulkItem> CancelAfter(CancellationTokenSource cts, int rows)
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

    [Fact]
    public void Enum_columns_are_rejected_with_guidance()
    {
        var ex = Assert.Throws<ArgumentException>(() => SqlBulkInsertPlan<BulkItem>.Create("dbo.T").Column("C", _ => Color.Red));
        Assert.Contains("(int)", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dbo.Products; DROP TABLE dbo.Victims")]
    [InlineData("a.b.c")]
    [InlineData("dbo.[Products]")]
    public void Unsafe_table_names_are_rejected_when_the_plan_is_built(string table)
        => Assert.Throws<ArgumentException>(() => SqlBulkInsertPlan<BulkItem>.Create(table));
}
