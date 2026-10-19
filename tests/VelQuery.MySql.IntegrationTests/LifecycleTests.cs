using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using Xunit;

namespace VelQuery.MySql.IntegrationTests;

/// <summary>Timeouts, cancellation, server-side interruptions, streaming, pool hygiene and transaction ownership.</summary>
public sealed class LifecycleTests(MySqlFixture fixture) : IntegrationTest(fixture)
{
    // A self-join that runs for minutes; used where SLEEP() would return 1 instead of failing when interrupted.
    private const string SlowQuery = "SELECT count(*) FROM rows100k a JOIN rows100k b ON a.name < b.name";

    [Fact]
    public async Task Command_timeout_is_reported_as_timeout_with_the_provider_exception()
    {
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<VelQueryTimeoutException>(() =>
            session.Sql(SlowQuery).WithTimeout(TimeSpan.FromSeconds(1)).ExecuteAsync(Ct));

        Assert.Equal(MySqlErrorCode.CommandTimeoutExpired, Assert.IsType<MySqlException>(ex.InnerException).ErrorCode);
        Assert.Equal(1, await session.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
    }

    [Fact]
    public async Task Server_statement_time_limit_is_reported_as_timeout()
    {
        await using var session = Session();
        var sql = Fixture.IsMariaDb
            ? "SET STATEMENT max_statement_time = 0.5 FOR " + SlowQuery
            : SlowQuery.Replace("SELECT", "SELECT /*+ MAX_EXECUTION_TIME(500) */", StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<VelQueryTimeoutException>(() => session.ExecuteScalarAsync<long>(sql, cancellationToken: Ct));

        Assert.Equal(Fixture.IsMariaDb ? 1969 : 3024, Assert.IsType<MySqlException>(ex.InnerException).Number);
    }

    [Fact]
    public async Task Caller_cancellation_stops_a_running_query_quickly()
    {
        await using var session = Session();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(SlowQuery, cancellationToken: cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"cancellation took {stopwatch.Elapsed}");
        Assert.Equal(1, await session.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
    }

    [Fact]
    public async Task Interrupted_SLEEP_returns_1_on_MySQL_and_times_out_on_MariaDB()
    {
        // Server semantics, documented here because on MySQL it looks like a lost timeout: MySqlConnector enforces
        // CommandTimeout and cancellation with KILL QUERY, and MySQL completes an interrupted SLEEP() normally with result 1
        // instead of raising ER_QUERY_INTERRUPTED (MariaDB raises it). VelQuery reports what the server returned; statements
        // doing real work fail on both, as tested above.
        await using var session = Session();
        var sleep = session.Sql("SELECT SLEEP(5)").WithTimeout(TimeSpan.FromSeconds(1));

        if (Fixture.IsMariaDb)
        {
            await Assert.ThrowsAsync<VelQueryTimeoutException>(() => sleep.ExecuteScalarAsync<long>(Ct));
        }
        else
        {
            Assert.Equal(1L, await sleep.ExecuteScalarAsync<long>(Ct));
        }
    }

    [Fact]
    public async Task Query_killed_by_another_session_is_a_provider_error_not_a_timeout_or_cancellation()
    {
        await using var session = Session();
        await using (await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            var connectionId = await session.ExecuteScalarAsync<long>("SELECT CONNECTION_ID()", cancellationToken: Ct);
            var running = session.ExecuteScalarAsync<long>(SlowQuery, cancellationToken: Ct);
            await Task.Delay(500, Ct);
            await using (var admin = new MySqlConnection(Fixture.ConnectionString))
            {
                await admin.OpenAsync(Ct);
                await using var kill = new MySqlCommand("KILL QUERY " + connectionId, admin);
                await kill.ExecuteNonQueryAsync(Ct);
            }

            var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => running);
            Assert.Equal(MySqlErrorCode.QueryInterrupted, ((MySqlException)ex.InnerException!).ErrorCode);
        }
    }

    [Fact]
    public async Task Lock_wait_timeout_is_a_provider_error_with_the_error_code()
    {
        var table = await CreateTableAsync("id int PRIMARY KEY, v int NOT NULL");
        await using var setup = Session();
        await setup.ExecuteAsync($"INSERT INTO {table} VALUES (1, 0)", cancellationToken: Ct);

        await using var holder = new MySqlConnection(Fixture.ConnectionString);
        await holder.OpenAsync(Ct);
        await using var holderTransaction = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new MySqlCommand($"UPDATE {table} SET v = 1 WHERE id = 1", holder, holderTransaction))
        {
            await lockRow.ExecuteNonQueryAsync(Ct);
        }

        await using var session = Session();
        await using (await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            await session.ExecuteAsync("SET SESSION innodb_lock_wait_timeout = 1", cancellationToken: Ct);
            var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteAsync($"UPDATE {table} SET v = 2 WHERE id = 1", cancellationToken: Ct));
            Assert.Equal(MySqlErrorCode.LockWaitTimeout, ((MySqlException)ex.InnerException!).ErrorCode);
        }

        await holderTransaction.RollbackAsync(Ct);
    }

    [Fact]
    public async Task Deadlock_victim_gets_a_provider_error_with_the_error_code()
    {
        var table = await CreateTableAsync("id int PRIMARY KEY, v int NOT NULL");
        await using var setup = Session();
        await setup.ExecuteAsync($"INSERT INTO {table} VALUES (1, 0), (2, 0)", cancellationToken: Ct);
        var bulky = await CreateTableAsync("id int PRIMARY KEY");

        // The other transaction modifies more rows, so InnoDB picks the VelQuery session as the (cheaper) victim.
        await using var other = new MySqlConnection(Fixture.ConnectionString);
        await other.OpenAsync(Ct);
        await using var otherTransaction = await other.BeginTransactionAsync(Ct);
        async Task OtherAsync(string sql)
        {
            await using var command = new MySqlCommand(sql, other, otherTransaction);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await OtherAsync($"INSERT INTO {bulky} SELECT id FROM rows100");
        await OtherAsync($"UPDATE {table} SET v = 1 WHERE id = 1");

        await using var session = Session();
        await using (await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            await session.ExecuteAsync($"UPDATE {table} SET v = 2 WHERE id = 2", cancellationToken: Ct);
            var blocked = OtherAsync($"UPDATE {table} SET v = 1 WHERE id = 2");
            await Task.Delay(300, Ct);

            var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteAsync($"UPDATE {table} SET v = 2 WHERE id = 1", cancellationToken: Ct));

            Assert.Equal(MySqlErrorCode.LockDeadlock, ((MySqlException)ex.InnerException!).ErrorCode);
            await blocked;
        }

        await otherTransaction.RollbackAsync(Ct);
    }

    [Fact]
    public async Task Streaming_yields_every_row_and_early_exit_returns_the_connection()
    {
        await using var dataSource = IsolatedPoolDataSource(maxPoolSize: 1, poolTag: "stream");
        await using (var session = dataSource.CreateSession())
        {
            long count = 0;
            await foreach (var row in session.StreamAsync<long>("SELECT id FROM rows100k ORDER BY id", null, Ct))
            {
                Assert.Equal(++count, row);
            }

            Assert.Equal(MySqlFixture.LargeRowCount, count);

            var stopwatch = Stopwatch.StartNew();
            await foreach (var _ in session.StreamAsync<long>("SELECT id FROM rows100k ORDER BY id", null, Ct))
            {
                break;
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"early exit took {stopwatch.Elapsed}");
        }

        await using var next = dataSource.CreateSession();
        Assert.Equal(1, await next.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
    }

    [Fact]
    public async Task Faults_cancellations_and_early_exits_do_not_leak_connections()
    {
        await using var dataSource = IsolatedPoolDataSource(maxPoolSize: 2, poolTag: "hygiene");
        for (var i = 0; i < 60; i++)
        {
            await using var session = dataSource.CreateSession();
            switch (i % 4)
            {
                case 0:
                    await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteAsync("SELECT * FROM no_such_table", cancellationToken: Ct));
                    break;
                case 1:
                    await Assert.ThrowsAsync<VelQueryMappingException>(() => session.QueryAsync<int>("SELECT category FROM rows100 WHERE id = 10", null, Ct));
                    break;
                case 2:
                    using (var cts = new CancellationTokenSource())
                    {
                        await cts.CancelAsync();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync("SELECT 1", cancellationToken: cts.Token));
                    }

                    break;
                default:
                    await foreach (var _ in session.StreamAsync<long>("SELECT id FROM rows100k", null, Ct))
                    {
                        break;
                    }

                    break;
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var session = dataSource.CreateSession();
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
            Assert.Equal(1, await session.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
        }));
    }

    [Fact]
    public async Task Session_transaction_commits_or_rolls_back_on_dispose()
    {
        var table = await CreateTableAsync("id int PRIMARY KEY");
        await using var session = Session();

        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            await session.ExecuteAsync($"INSERT INTO {table} VALUES (1)", cancellationToken: Ct);
            await transaction.CommitAsync(Ct);
        }

        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            await session.ExecuteAsync($"INSERT INTO {table} VALUES (2)", cancellationToken: Ct);
        }

        Assert.Equal(1L, await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct));
    }

    [Fact]
    public async Task Caller_owned_connection_and_transaction_are_used_but_never_completed_or_closed()
    {
        var table = await CreateTableAsync("id int PRIMARY KEY");
        await using var connection = new MySqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var session = Fixture.CreateDataSource().CreateSession(connection, transaction))
        {
            await session.ExecuteAsync($"INSERT INTO {table} VALUES (1)", cancellationToken: Ct);
            await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteAsync($"INSERT INTO {table} VALUES (1)", cancellationToken: Ct));
        }

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        await transaction.RollbackAsync(Ct);
        await using var check = Session();
        Assert.Equal(0L, await check.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct));
    }
}
