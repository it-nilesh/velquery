using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VelQuery.Sqlite.Bulk;
using Xunit;

namespace VelQuery.Sqlite.IntegrationTests;

public sealed record LiteItem(int Id, string Name, decimal? Price, int Stock, DateTime CreatedUtc, Guid Key);

public sealed record LiteStoredItem(int Id, string Name, decimal? Price, int Stock, DateTime CreatedUtc, Guid Key);

[VelQuerySql("sqlite.bulk-read", "SELECT id, name, price, stock, created_utc AS CreatedUtc, key FROM bulk_read ORDER BY id")]
public static partial class LiteBulkReadBack
{
    public static partial Task<IReadOnlyList<LiteStoredItem>> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

/// <summary>Prepared-INSERT bulk load: all-or-nothing, constraint-checked, session-transaction aware.</summary>
public sealed class BulkInsertTests(SqliteFixture fixture) : IntegrationTest(fixture)
{
    private const string Columns = "id INTEGER PRIMARY KEY, name TEXT NOT NULL, price TEXT NULL DEFAULT '9.99', stock INTEGER NOT NULL CHECK (stock >= 0), created_utc TEXT NOT NULL, key BLOB NOT NULL";

    private static SqliteBulkInsertPlan<LiteItem> Plan(string table) => SqliteBulkInsertPlan<LiteItem>.Create(table)
        .Column("id", r => r.Id)
        .Column("name", r => r.Name)
        .Column("price", r => r.Price)
        .Column("stock", r => r.Stock)
        .Column("created_utc", r => r.CreatedUtc)
        .Column("key", r => r.Key, SqliteType.Blob);

    private static readonly DateTime Created = new(2026, 10, 11, 3, 0, 0, DateTimeKind.Utc);

    private static Guid KeyOf(int id) => new(id, 0, 0, new byte[8]);

    private static IEnumerable<LiteItem> Items(int count, int failAt = -1)
        => Enumerable.Range(1, count).Select(i => new LiteItem(i, "item-" + i, i % 5 == 0 ? null : i * 1.25m, i == failAt ? -1 : i, Created, KeyOf(i)));

    private async Task<(long Count, long NullPrices, decimal Sum)> StatsAsync(string table)
    {
        await using var session = Session();
        return (await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct),
            await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table} WHERE price IS NULL", cancellationToken: Ct),
            (await session.QueryAsync<decimal>($"SELECT price FROM {table} WHERE price IS NOT NULL", null, Ct)).Sum());
    }

    [Fact]
    public async Task Writes_every_row_with_exact_values_and_keeps_nulls()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();

        Assert.Equal(50_000, await session.BulkInsertAsync(Plan(table), Items(50_000), Ct));

        Assert.Equal((50_000L, 10_000L, Items(50_000).Sum(i => i.Price ?? 0m)), await StatsAsync(table));
        // NULL is written as NULL, not replaced by the column default.
        Assert.Equal(0L, await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table} WHERE price = '9.99'", cancellationToken: Ct));
        Assert.Equal("blob|16|text", await session.ExecuteScalarAsync<string>($"SELECT typeof(key) || '|' || length(key) || '|' || typeof(created_utc) FROM {table} WHERE id = 1", cancellationToken: Ct));
    }

    [Fact]
    public async Task Values_read_back_exactly_through_a_generated_contract()
    {
        await using var session = Session();
        await session.ExecuteAsync($"DROP TABLE IF EXISTS bulk_read; CREATE TABLE bulk_read ({Columns})", cancellationToken: Ct);
        var items = Items(1_000).ToList();

        await session.BulkInsertAsync(Plan("main.bulk_read"), items, Ct);

        var stored = await LiteBulkReadBack.ExecuteAsync(session, Ct);
        // DateTime.Kind is not stored by SQLite; the wall-clock value is exact.
        Assert.Equal(items.Select(i => new LiteStoredItem(i.Id, i.Name, i.Price, i.Stock, DateTime.SpecifyKind(i.CreatedUtc, DateTimeKind.Unspecified), i.Key)), stored);
    }

    [Fact]
    public async Task A_failing_row_rolls_back_every_row()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.BulkInsertAsync(Plan(table), Items(20_000, failAt: 15_000), Ct));

        var inner = Assert.IsType<SqliteException>(ex.InnerException);
        Assert.Equal(19, inner.SqliteErrorCode);
        Assert.Equal(275, inner.SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_CHECK
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

        var inner = Assert.IsType<SqliteException>(ex.InnerException);
        Assert.Equal(19, inner.SqliteErrorCode);
        Assert.Equal(1555, inner.SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_PRIMARYKEY
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
    public async Task A_failure_inside_a_session_transaction_undoes_only_the_load()
    {
        var table = await CreateTableAsync(Columns);
        await using (var session = Session())
        {
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
            await session.ExecuteAsync($"INSERT INTO {table} (id, name, stock, created_utc, key) VALUES (-1, 'kept', 1, 'now', x'00')", cancellationToken: Ct);

            await Assert.ThrowsAsync<VelQueryProviderException>(() => session.BulkInsertAsync(Plan(table), Items(5_000, failAt: 4_000), Ct));

            // The session transaction is still active and only the earlier insert remains in it.
            Assert.Equal(1L, await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct));
            Assert.Equal(100, await session.BulkInsertAsync(Plan(table), Items(100), Ct));
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(101L, (await StatsAsync(table)).Count);
    }

    [Fact]
    public async Task Streams_rows_from_an_async_source()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();

        Assert.Equal(30_000, await session.BulkInsertAsync(Plan(table), ProduceAsync(30_000, Ct), Ct));
        Assert.Equal(30_000L, (await StatsAsync(table)).Count);
    }

    private static async IAsyncEnumerable<LiteItem> ProduceAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
    public async Task Cancellation_rolls_back_and_returns_the_connection()
    {
        var table = await CreateTableAsync(Columns);
        var counting = new CountingDataSource(Fixture.ConnectionString);
        await using var dataSource = VelQuerySqlite.CreateDataSource(counting, ownsDataSource: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using (var session = dataSource.CreateSession())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.BulkInsertAsync(Plan(table), CancelAfter(cts, 20_000), cts.Token));
        }

        Assert.Equal(0, counting.Outstanding);
        Assert.Equal(0L, (await StatsAsync(table)).Count);
    }

    [Fact]
    public async Task Cancellation_of_an_async_source_inside_a_session_transaction_keeps_the_transaction()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.BulkInsertAsync(Plan(table), SlowAsync(cts.Token), cts.Token));

        Assert.Equal(0L, await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct));
        Assert.Equal(10, await session.BulkInsertAsync(Plan(table), Items(10), Ct));
        await transaction.CommitAsync(Ct);
    }

    private static async IAsyncEnumerable<LiteItem> SlowAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in Items(1_000_000))
        {
            if (item.Id % 100 == 0)
            {
                await Task.Delay(5, cancellationToken);
            }

            yield return item;
        }
    }

    private static IEnumerable<LiteItem> CancelAfter(CancellationTokenSource cts, int rows)
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

    [Fact]
    public async Task A_locked_database_times_out_the_load()
    {
        var connectionString = Fixture.NewDatabase("bulk-locked");
        await using var holder = new SqliteConnection(connectionString);
        await holder.OpenAsync(Ct);
        await using (var command = holder.CreateCommand())
        {
            command.CommandText = $"CREATE TABLE items ({Columns}); BEGIN IMMEDIATE; INSERT INTO items (id, name, stock, created_utc, key) VALUES (-1, 'x', 1, 'now', x'00');";
            await command.ExecuteNonQueryAsync(Ct);
        }

        // BEGIN IMMEDIATE waits for the write lock using the connection's default timeout.
        await using var dataSource = VelQuerySqlite.CreateDataSource(connectionString + ";Default Timeout=1");
        await using var session = dataSource.CreateSession();

        var ex = await Assert.ThrowsAsync<VelQueryTimeoutException>(() => session.BulkInsertAsync(Plan("items"), Items(10), Ct));

        Assert.Equal(5, Assert.IsType<SqliteException>(ex.InnerException).SqliteErrorCode);
    }

    [Fact]
    public async Task An_empty_source_writes_nothing_and_an_empty_plan_is_rejected()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();

        Assert.Equal(0, await session.BulkInsertAsync(Plan(table), [], Ct));
        await Assert.ThrowsAsync<VelQueryValidationException>(() => session.BulkInsertAsync(SqliteBulkInsertPlan<LiteItem>.Create(table), Items(1), Ct));
    }

    [Fact]
    public void Plan_builds_a_quoted_parameterized_statement()
    {
        var plan = SqliteBulkInsertPlan<LiteItem>.Create("main.items").Column("id", r => r.Id).Column("name", r => r.Name);

        Assert.Equal("INSERT INTO \"main\".\"items\" (\"id\", \"name\") VALUES (@p0, @p1)", plan.InsertCommand);
        Assert.Equal("bulk:main.items", plan.QueryId);
        Assert.Equal("custom", plan.WithQueryId("custom").QueryId);
        Assert.Throws<ArgumentException>(() => plan.Column("ID", r => r.Id));
    }

    private enum Color
    {
        Red = 1,
    }

    [Theory]
    [InlineData("items; DROP TABLE victims")]
    [InlineData("a.b.c")]
    [InlineData("\"items\"")]
    [InlineData("1items")]
    public void Unsafe_names_and_enum_columns_are_rejected_when_the_plan_is_built(string table)
    {
        Assert.Throws<ArgumentException>(() => SqliteBulkInsertPlan<LiteItem>.Create(table));
        Assert.Throws<ArgumentException>(() => SqliteBulkInsertPlan<LiteItem>.Create("items").Column("c", _ => Color.Red));
        Assert.Throws<ArgumentException>(() => SqliteBulkInsertPlan<LiteItem>.Create("items").Column("c", _ => (Color?)null));
        Assert.Throws<ArgumentException>(() => SqliteBulkInsertPlan<LiteItem>.Create("items").Column("c; --", r => r.Id));
    }
}
