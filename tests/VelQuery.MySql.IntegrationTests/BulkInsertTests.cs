using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using VelQuery.MySql.Bulk;
using Xunit;

namespace VelQuery.MySql.IntegrationTests;

public sealed record MyItem(int Id, string Name, decimal? Price, int Stock, DateTime CreatedUtc);

/// <summary>
/// LOAD DATA LOCAL bulk insert: all-or-nothing, and never silently skipping or coercing rows (which MySQL does for LOCAL
/// loads even in strict mode).
/// </summary>
public sealed class BulkInsertTests(MySqlFixture fixture) : IntegrationTest(fixture)
{
    private const string Columns = "id int PRIMARY KEY, name varchar(20) NOT NULL, price decimal(18,2) NULL DEFAULT 9.99, stock int NOT NULL CHECK (stock >= 0), created_utc datetime(6) NOT NULL";

    private static MySqlBulkInsertPlan<MyItem> Plan(string table) => MySqlBulkInsertPlan<MyItem>.Create(table)
        .Column("id", r => r.Id)
        .Column("name", r => r.Name)
        .Column("price", r => r.Price)
        .Column("stock", r => r.Stock)
        .Column("created_utc", r => r.CreatedUtc);

    private static readonly DateTime Created = new(2026, 10, 11, 3, 0, 0, 123, 456, DateTimeKind.Utc);

    private static IEnumerable<MyItem> Items(int count, int failAt = -1, string? failName = null)
        => Enumerable.Range(1, count).Select(i => new MyItem(i, i == failAt && failName is not null ? failName : "item-" + i,
            i % 5 == 0 ? null : i * 1.25m, i == failAt && failName is null ? -1 : i, Created));

    private async Task<(long Count, long NullPrices, decimal Sum)> StatsAsync(string table, IVelQuerySession? session = null)
    {
        await using var own = Session();
        var s = session ?? own;
        return (await s.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct),
            await s.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table} WHERE price IS NULL", cancellationToken: Ct),
            await s.ExecuteScalarAsync<decimal?>($"SELECT sum(price) FROM {table}", cancellationToken: Ct) ?? 0m);
    }

    [Fact]
    public async Task Writes_every_row_with_exact_values_and_keeps_nulls()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();

        Assert.Equal(50_000, await session.BulkInsertAsync(Plan(table), Items(50_000), Ct));

        // NULL stays NULL instead of taking the column default (9.99).
        Assert.Equal((50_000L, 10_000L, Items(50_000).Sum(i => i.Price ?? 0m)), await StatsAsync(table));
        Assert.Equal(Created, DateTime.SpecifyKind(await session.ExecuteScalarAsync<DateTime>($"SELECT max(created_utc) FROM {table}", cancellationToken: Ct), DateTimeKind.Utc));
    }

    public sealed record Wide(int Id, string? Text, byte[]? Bytes, Guid Uid, DateOnly Day, TimeOnly Time, bool Flag, ulong Big, double Ratio, string Json);

    [Fact]
    public async Task Writes_text_binary_guid_and_temporal_values_faithfully()
    {
        var table = await CreateTableAsync("id int PRIMARY KEY, txt text NULL, bytes longblob NULL, uid char(36) NOT NULL, day date NOT NULL, tm time(6) NOT NULL, flag tinyint(1) NOT NULL, big bigint unsigned NOT NULL, ratio double NOT NULL, js json NOT NULL");
        var plan = MySqlBulkInsertPlan<Wide>.Create(table)
            .Column("id", r => r.Id).Column("txt", r => r.Text).Column("bytes", r => r.Bytes).Column("uid", r => r.Uid)
            .Column("day", r => r.Day).Column("tm", r => r.Time).Column("flag", r => r.Flag).Column("big", r => r.Big)
            .Column("ratio", r => r.Ratio).Column("js", r => r.Json);
        var tricky = "tab\there, newline\nhere, back\\slash, quote' \"dq\" 💡 ✓ " + new string('é', 3000);
        var rows = new[]
        {
            new Wide(1, tricky, [0x00, 0xff, 0x10, 0x5c, 0x09, 0x0a], Guid.NewGuid(), new DateOnly(2026, 10, 11), new TimeOnly(13, 45, 7, 500), true, ulong.MaxValue, 0.1, "{\"a\": [1, 2]}"),
            new Wide(2, null, null, Guid.NewGuid(), DateOnly.MinValue, TimeOnly.MinValue, false, 0, -1e300, "null"),
            new Wide(3, "\\N", [], Guid.Empty, new DateOnly(9999, 12, 31), new TimeOnly(23, 59, 59), false, 1, double.Epsilon, "[]"),
        };
        await using var session = Session();

        Assert.Equal(3, await session.BulkInsertAsync(plan, rows, Ct));

        var read = await session.QueryAsync<Wide>($"SELECT id, txt AS text, bytes, uid, day, tm AS time, flag, big, ratio, js AS json FROM {table} ORDER BY id", null, Ct);
        Assert.Equal(rows.Length, read.Count);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(rows[i] with { Bytes = null, Json = "" }, read[i] with { Bytes = null, Json = "" });
            Assert.Equal(rows[i].Bytes, read[i].Bytes);
        }

        // "\N" is a literal two-character string, not NULL.
        Assert.Equal("\\N", read[2].Text);
    }

    [Fact]
    public async Task A_check_violation_fails_the_load_and_leaves_no_rows()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();

        // MySQL skips the violating row of a LOCAL load (IGNORE semantics) and keeps the rest; VelQuery rolls it all back.
        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.BulkInsertAsync(Plan(table), Items(20_000, failAt: 15_000), Ct));

        var bulk = Assert.IsType<MySqlBulkInsertException>(ex.InnerException);
        Assert.Equal(20_000, bulk.RowsSent);
        Assert.Contains(bulk.Warnings, w => w.Code is 3819 or 4025); // ER_CHECK_CONSTRAINT_VIOLATED (MySQL) / ER_CONSTRAINT_FAILED (MariaDB)
        Assert.Equal("bulk:" + table, ex.QueryId);
        Assert.Equal(0L, (await StatsAsync(table)).Count);
    }

    [Fact]
    public async Task Duplicate_keys_surface_as_provider_errors_and_nothing_is_kept()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();
        await session.BulkInsertAsync(Plan(table), Items(10), Ct);

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.BulkInsertAsync(Plan(table), Items(20), Ct));

        var bulk = Assert.IsType<MySqlBulkInsertException>(ex.InnerException);
        Assert.Contains(bulk.Warnings, w => w.Code == (int)MySqlErrorCode.DuplicateKeyEntry);
        Assert.Equal(MySqlErrorCode.BulkCopyFailed, Assert.IsType<MySqlException>(bulk.InnerException).ErrorCode);
        // Rows 11-20 were valid but are rolled back with the rest of the load.
        Assert.Equal(10L, (await StatsAsync(table)).Count);
    }

    [Fact]
    public async Task Values_the_server_would_coerce_fail_the_load_instead()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();

        // An over-long string is truncated, and NULL into NOT NULL becomes '' - with only a warning - in a LOCAL load.
        var truncated = await Assert.ThrowsAsync<VelQueryProviderException>(() =>
            session.BulkInsertAsync(Plan(table), Items(100, failAt: 50, failName: new string('x', 21)), Ct));
        Assert.Contains(Assert.IsType<MySqlBulkInsertException>(truncated.InnerException).Warnings, w => w.Code == (int)MySqlErrorCode.WarningDataTruncated);
        Assert.DoesNotContain("xxxxxxxxxxxxxxxxxxxxx", truncated.InnerException!.Message, StringComparison.Ordinal);

        var nullName = await Assert.ThrowsAsync<VelQueryProviderException>(() =>
            session.BulkInsertAsync(Plan(table), Items(100).Select(i => i.Id == 7 ? i with { Name = null! } : i), Ct));
        Assert.Contains(Assert.IsType<MySqlBulkInsertException>(nullName.InnerException).Warnings, w => w.Code == (int)MySqlErrorCode.WarningNullToNotNull);

        Assert.Equal(0L, (await StatsAsync(table)).Count);
    }

    public sealed record Keyed(int Id, Guid Key);

    [Fact]
    public async Task A_guid_into_binary16_without_a_binary_GuidFormat_fails_instead_of_storing_null()
    {
        var table = await CreateTableAsync("id int PRIMARY KEY, k binary(16) NULL");
        var plan = MySqlBulkInsertPlan<Keyed>.Create(table).Column("id", r => r.Id).Column("k", r => r.Key);
        await using var session = Session();

        // MySQL would store NULL with a warning; MariaDB stores NULL silently. VelQuery checks the destination type first.
        var ex = await Assert.ThrowsAsync<VelQueryValidationException>(() => session.BulkInsertAsync(plan, [new Keyed(1, Guid.NewGuid())], Ct));
        Assert.Contains("GuidFormat", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0L, await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct));

        var bytesIntoText = MySqlBulkInsertPlan<Keyed>.Create(table).Column("id", r => r.Id).Column("k", r => r.Key.ToByteArray());
        await session.ExecuteAsync($"ALTER TABLE {table} MODIFY k varchar(64) NULL", cancellationToken: Ct);
        await Assert.ThrowsAsync<VelQueryValidationException>(() => session.BulkInsertAsync(bytesIntoText, [new Keyed(1, Guid.NewGuid())], Ct));
        await session.ExecuteAsync($"ALTER TABLE {table} MODIFY k binary(16) NULL", cancellationToken: Ct);

        await using var binary = Fixture.CreateDataSource(b => b.GuidFormat = MySqlGuidFormat.Binary16);
        await using var binarySession = binary.CreateSession();
        var key = Guid.NewGuid();
        Assert.Equal(1, await binarySession.BulkInsertAsync(plan, [new Keyed(1, key)], Ct));
        Assert.Equal(key, await binarySession.ExecuteScalarAsync<Guid>($"SELECT k FROM {table}", cancellationToken: Ct));
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
    public async Task A_failed_load_inside_a_session_transaction_is_undone_but_the_transaction_continues()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();
        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            Assert.Equal(100, await session.BulkInsertAsync(Plan(table), Items(100), Ct));

            await Assert.ThrowsAsync<VelQueryProviderException>(() =>
                session.BulkInsertAsync(Plan(table), Items(200).Where(i => i.Id > 50), Ct));

            // Only the failed load was undone (savepoint); earlier work and the transaction itself are untouched.
            Assert.Equal(100L, (await StatsAsync(table, session)).Count);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(100L, (await StatsAsync(table)).Count);
    }

    [Fact]
    public async Task Caller_owned_transaction_is_enlisted_and_never_completed()
    {
        var table = await CreateTableAsync(Columns);
        await using var connection = new MySqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var session = Fixture.CreateDataSource().CreateSession(connection, transaction))
        {
            Assert.Equal(10, await session.BulkInsertAsync(Plan(table), Items(10), Ct));
        }

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        await using (var inside = new MySqlCommand($"SELECT count(*) FROM {table}", connection, transaction))
        {
            Assert.Equal(10L, await inside.ExecuteScalarAsync(Ct));
        }

        await transaction.RollbackAsync(Ct);
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

    private static async IAsyncEnumerable<MyItem> ProduceAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
    public async Task Cancellation_aborts_the_load_and_returns_the_connection()
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

    private static IEnumerable<MyItem> CancelAfter(CancellationTokenSource cts, int rows)
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
    public async Task Source_exceptions_propagate_and_roll_back()
    {
        var table = await CreateTableAsync(Columns);
        await using var session = Session();

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.BulkInsertAsync(Plan(table), Throwing(), Ct));

        Assert.Equal(0L, (await StatsAsync(table)).Count);

        static IEnumerable<MyItem> Throwing()
        {
            foreach (var item in Items(5_000))
            {
                yield return item;
            }

            throw new InvalidOperationException("source failed");
        }
    }

    [Fact]
    public async Task Local_infile_disabled_on_the_client_is_an_unsupported_capability()
    {
        var table = await CreateTableAsync(Columns);
        await using var dataSource = Fixture.CreateDataSource(b => b.AllowLoadLocalInfile = false);
        await using var session = dataSource.CreateSession();

        var ex = await Assert.ThrowsAsync<VelQueryUnsupportedCapabilityException>(() => session.BulkInsertAsync(Plan(table), Items(10), Ct));

        Assert.Contains("AllowLoadLocalInfile", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_infile_disabled_on_the_server_is_an_unsupported_capability()
    {
        var table = await CreateTableAsync(Columns);
        await using var admin = Session();
        await admin.ExecuteAsync("SET GLOBAL local_infile = 0", cancellationToken: Ct);
        try
        {
            await using var session = Session();
            var ex = await Assert.ThrowsAsync<VelQueryUnsupportedCapabilityException>(() => session.BulkInsertAsync(Plan(table), Items(10), Ct));
            Assert.Contains("local_infile", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await admin.ExecuteAsync("SET GLOBAL local_infile = 1", cancellationToken: Ct);
        }

        Assert.Equal(0L, (await StatsAsync(table)).Count);
    }

    [Fact]
    public async Task Reserved_word_columns_and_database_qualified_tables_work()
    {
        var table = await CreateTableAsync("`order` int PRIMARY KEY, `select` varchar(10) NOT NULL");
        await using var session = Session();
        var database = await session.ExecuteScalarAsync<string>("SELECT DATABASE()", cancellationToken: Ct);
        var plan = MySqlBulkInsertPlan<(int Order, string Select)>.Create(database + "." + table)
            .Column("order", r => r.Order).Column("select", r => r.Select);

        Assert.Equal($"`{database}`.`{table}`", plan.QuotedTable);
        Assert.Equal(2, await session.BulkInsertAsync(plan, [(1, "a"), (2, "b")], Ct));
        Assert.Equal("b", await session.ExecuteScalarAsync<string>($"SELECT `select` FROM {table} WHERE `order` = 2", cancellationToken: Ct));
    }

    private enum Color
    {
        Red = 1,
    }

    [Theory]
    [InlineData("items; DROP TABLE victims")]
    [InlineData("a.b.c")]
    [InlineData("`items`")]
    [InlineData("items`")]
    public void Unsafe_names_and_enum_columns_are_rejected_when_the_plan_is_built(string table)
    {
        Assert.Throws<ArgumentException>(() => MySqlBulkInsertPlan<MyItem>.Create(table));
        Assert.Throws<ArgumentException>(() => MySqlBulkInsertPlan<MyItem>.Create("items").Column("c", _ => Color.Red));
        Assert.Throws<ArgumentException>(() => MySqlBulkInsertPlan<MyItem>.Create("items").Column("c`; --", r => r.Id));
        Assert.Throws<ArgumentException>(() => MySqlBulkInsertPlan<MyItem>.Create("items").Column("id", r => r.Id).Column("ID", r => r.Id));
    }
}
