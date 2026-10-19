using System;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace VelQuery.Sqlite.IntegrationTests;

/// <summary>Lock timeouts, cancellation, streaming, connection hygiene and transaction ownership on SQLite.</summary>
public sealed class LifecycleTests(SqliteFixture fixture) : IntegrationTest(fixture)
{
    /// <summary>Opens a connection that holds the write lock on a fresh database file until disposed.</summary>
    private async Task<(string ConnectionString, SqliteConnection Holder)> LockedDatabaseAsync()
    {
        var connectionString = Fixture.NewDatabase("locked");
        var holder = new SqliteConnection(connectionString);
        await holder.OpenAsync(Ct);
        await using var command = holder.CreateCommand();
        command.CommandText = "CREATE TABLE x (id INTEGER PRIMARY KEY); BEGIN IMMEDIATE; INSERT INTO x VALUES (1);";
        await command.ExecuteNonQueryAsync(Ct);
        return (connectionString, holder);
    }

    [Fact]
    public async Task Busy_lock_wait_past_the_command_timeout_is_reported_as_timeout()
    {
        var (connectionString, holder) = await LockedDatabaseAsync();
        await using (holder)
        await using (var dataSource = VelQuerySqlite.CreateDataSource(connectionString))
        await using (var session = dataSource.CreateSession())
        {
            var stopwatch = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<VelQueryTimeoutException>(() =>
                session.Sql("INSERT INTO x VALUES (2)").WithTimeout(TimeSpan.FromSeconds(1)).ExecuteAsync(Ct));

            Assert.Equal(5, ((SqliteException)ex.InnerException!).SqliteErrorCode); // SQLITE_BUSY
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Shared_cache_table_lock_is_reported_as_timeout()
    {
        var shared = $"Data Source=vq-locked-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var holder = new SqliteConnection(shared);
        await holder.OpenAsync(Ct);
        await using (var command = holder.CreateCommand())
        {
            command.CommandText = "CREATE TABLE y (id INTEGER PRIMARY KEY); BEGIN; INSERT INTO y VALUES (1);";
            await command.ExecuteNonQueryAsync(Ct);
        }

        await using var dataSource = VelQuerySqlite.CreateDataSource(shared, new VelQueryOptions { DefaultTimeout = TimeSpan.FromSeconds(1) });
        await using var session = dataSource.CreateSession();

        var ex = await Assert.ThrowsAsync<VelQueryTimeoutException>(() => session.ExecuteScalarAsync<long>("SELECT count(*) FROM y", cancellationToken: Ct));

        Assert.Equal(6, ((SqliteException)ex.InnerException!).SqliteErrorCode); // SQLITE_LOCKED (extended: LOCKED_SHAREDCACHE)
    }

    [Fact]
    public async Task Caller_cancellation_wins_over_a_lock_timeout_but_is_observed_only_when_the_wait_ends()
    {
        var (connectionString, holder) = await LockedDatabaseAsync();
        await using (holder)
        await using (var dataSource = VelQuerySqlite.CreateDataSource(connectionString))
        await using (var session = dataSource.CreateSession())
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(200));

            // Microsoft.Data.Sqlite does not interrupt its busy wait for the token; VelQuery reports the eventual
            // SQLITE_BUSY as the caller's cancellation, not as a timeout, because the token was cancelled.
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.Sql("INSERT INTO x VALUES (2)").WithTimeout(TimeSpan.FromSeconds(2)).ExecuteAsync(cts.Token));

            Assert.Equal(5, Assert.IsType<SqliteException>(ex.InnerException).SqliteErrorCode);
        }
    }

    [Fact]
    public async Task Pre_cancelled_tokens_never_touch_the_database()
    {
        var counting = new CountingDataSource(Fixture.ConnectionString);
        await using var dataSource = VelQuerySqlite.CreateDataSource(counting, ownsDataSource: true);
        await using var session = dataSource.CreateSession();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync("SELECT 1", cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.QueryAsync<long>("SELECT 1", null, cts.Token));

        Assert.Equal(0, counting.Created);
    }

    [Fact]
    public async Task Cancellation_stops_streaming_between_rows()
    {
        var counting = new CountingDataSource(Fixture.ConnectionString);
        await using var dataSource = VelQuerySqlite.CreateDataSource(counting, ownsDataSource: true);
        await using var session = dataSource.CreateSession();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        long seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in session.StreamAsync<long>("SELECT id FROM rows100k ORDER BY id", null, cts.Token))
            {
                if (++seen == 1_000)
                {
                    await cts.CancelAsync();
                }
            }
        });

        Assert.Equal(1_000, seen);
        Assert.Equal(0, counting.Outstanding);
    }

    [Fact]
    public async Task A_single_long_running_step_is_not_interrupted_by_the_token_or_the_timeout()
    {
        // Microsoft.Data.Sqlite neither interrupts a running statement for the token nor applies CommandTimeout to it
        // (the timeout only bounds lock waits). An aggregate does all its work in its first step, so cancellation is
        // observed only when the first row is returned.
        await using var session = Session();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteScalarAsync<long>(SlowAggregate(40), cancellationToken: cts.Token));
        var cancelledAfter = stopwatch.Elapsed;

        stopwatch.Restart();
        Assert.Equal(40_000_000L, await session.Sql(SlowAggregate(40)).WithTimeout(TimeSpan.FromSeconds(1)).ExecuteScalarAsync<long>(Ct));
        var completedAfter = stopwatch.Elapsed;

        Assert.True(completedAfter > TimeSpan.FromSeconds(1), $"the query finished in {completedAfter}; pick a slower query");
        Assert.True(cancelledAfter > completedAfter / 2, $"cancellation took {cancelledAfter}, the full query {completedAfter}: the driver now interrupts; update the docs");
    }

    [Fact]
    public async Task An_interrupt_not_caused_by_the_caller_is_reported_as_provider_cancellation()
    {
        await using var connection = new SqliteConnection(Fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var session = Fixture.CreateDataSource().CreateSession(connection);
        using var interrupt = new Timer(_ => SQLitePCL.raw.sqlite3_interrupt(connection.Handle), null, 200, Timeout.Infinite);

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => session.ExecuteScalarAsync<long>(SlowAggregate(200), cancellationToken: Ct));

        Assert.Contains("cancelled by the provider", ex.Message, StringComparison.Ordinal);
        Assert.Equal(9, Assert.IsType<SqliteException>(ex.InnerException).SqliteErrorCode); // SQLITE_INTERRUPT
        Assert.Equal(1L, await session.ExecuteScalarAsync<long>("SELECT 1", cancellationToken: Ct));
    }

    [Fact]
    public void Classification_covers_extended_result_codes()
    {
        var provider = SqliteProvider.Instance;

        Assert.Equal(Providers.ProviderErrorKind.Timeout, provider.Classify(new SqliteException("busy", 5, 517))); // BUSY_SNAPSHOT
        Assert.Equal(Providers.ProviderErrorKind.Timeout, provider.Classify(new SqliteException("locked", 6, 262)));
        Assert.Equal(Providers.ProviderErrorKind.Cancelled, provider.Classify(new SqliteException("interrupted", 9)));
        Assert.Equal(Providers.ProviderErrorKind.Other, provider.Classify(new SqliteException("constraint", 19, 1555)));
        Assert.Equal(Providers.ProviderErrorKind.Other, provider.Classify(new InvalidOperationException()));
    }

    [Fact]
    public async Task Streaming_yields_every_row_and_early_exit_releases_connection_and_read_lock()
    {
        // A rollback-journal database: an unreleased reader would hold a SHARED lock and block an exclusive writer.
        var connectionString = Fixture.NewDatabase("stream");
        await using (var setup = new SqliteConnection(connectionString))
        {
            await setup.OpenAsync(Ct);
            await using var command = setup.CreateCommand();
            command.CommandText = """
                CREATE TABLE s (id INTEGER PRIMARY KEY);
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 100000) INSERT INTO s SELECT x FROM n;
                """;
            await command.ExecuteNonQueryAsync(Ct);
        }

        var counting = new CountingDataSource(connectionString);
        await using var dataSource = VelQuerySqlite.CreateDataSource(counting, ownsDataSource: true);
        await using (var session = dataSource.CreateSession())
        {
            long count = 0;
            await foreach (var row in session.StreamAsync<long>("SELECT id FROM s ORDER BY id", null, Ct))
            {
                Assert.Equal(++count, row);
            }

            Assert.Equal(SqliteFixture.LargeRowCount, count);

            var stopwatch = Stopwatch.StartNew();
            await foreach (var _ in session.StreamAsync<long>("SELECT id FROM s ORDER BY id", null, Ct))
            {
                break;
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"early exit took {stopwatch.Elapsed}");
        }

        Assert.Equal(0, counting.Outstanding);
        await using var writer = new SqliteConnection(connectionString + ";Default Timeout=1");
        await writer.OpenAsync(Ct);
        await using var exclusive = writer.CreateCommand();
        exclusive.CommandText = "BEGIN EXCLUSIVE; DELETE FROM s; COMMIT;";
        await exclusive.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task Faults_cancellations_and_early_exits_do_not_leak_connections()
    {
        var counting = new CountingDataSource(Fixture.ConnectionString);
        await using var dataSource = VelQuerySqlite.CreateDataSource(counting, ownsDataSource: true);
        for (var i = 0; i < 60; i++)
        {
            await using var session = dataSource.CreateSession();
            switch (i % 6)
            {
                case 0:
                    await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteAsync("SELECT * FROM no_such_table", cancellationToken: Ct));
                    break;
                case 1:
                    await Assert.ThrowsAsync<VelQueryMappingException>(() => session.QueryAsync<int>("SELECT category FROM rows100 WHERE id = 10", null, Ct));
                    break;
                case 2:
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
                    {
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                        {
                            await foreach (var _ in session.StreamAsync<long>("SELECT id FROM rows100k", null, cts.Token))
                            {
                                await cts.CancelAsync();
                            }
                        });
                    }

                    break;
                case 3:
                    await foreach (var _ in session.StreamAsync<long>("SELECT id FROM rows100k", null, Ct))
                    {
                        break;
                    }

                    break;
                case 4:
                    await using (var multi = await session.QueryMultipleAsync("SELECT 1; SELECT 2", new QueryParameters(), Ct))
                    {
                        Assert.Equal(1L, await multi.ReadSingleAsync<long>(Ct));
                    }

                    break;
                default:
                    await Assert.ThrowsAsync<VelQueryProviderException>(async () =>
                    {
                        await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
                        await session.ExecuteAsync("INSERT INTO victims VALUES (1, 'duplicate')", cancellationToken: Ct);
                    });
                    break;
            }
        }

        Assert.True(counting.Created >= 60, $"only {counting.Created} connections were opened");
        Assert.Equal(0, counting.Outstanding);
    }

    [Fact]
    public async Task Session_transaction_commits_or_rolls_back_on_dispose()
    {
        var table = await CreateTableAsync("id INTEGER PRIMARY KEY");
        await using var session = Session();

        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            await session.ExecuteAsync($"INSERT INTO {table} VALUES (1)", cancellationToken: Ct);
            await transaction.CommitAsync(Ct);
        }

        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            await session.ExecuteAsync($"INSERT INTO {table} VALUES (2)", cancellationToken: Ct);
            Assert.Equal(2L, await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct));
        }

        Assert.Equal(1L, await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct));
    }

    [Fact]
    public async Task Session_transaction_is_isolated_from_other_connections_until_commit()
    {
        var table = await CreateTableAsync("id INTEGER PRIMARY KEY");
        await using var session = Session();
        await using var other = Session();

        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            await session.ExecuteAsync($"INSERT INTO {table} VALUES (1)", cancellationToken: Ct);
            // WAL mode: readers on other connections see the last committed state and are not blocked.
            Assert.Equal(0L, await other.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct));
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(1L, await other.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct));
    }

    [Fact]
    public async Task Caller_owned_connection_and_transaction_are_used_but_never_completed_or_closed()
    {
        var table = await CreateTableAsync("id INTEGER PRIMARY KEY");
        await using var connection = new SqliteConnection(Fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(Ct);
        await using (var session = Fixture.CreateDataSource().CreateSession(connection, transaction))
        {
            await session.ExecuteAsync($"INSERT INTO {table} VALUES (1)", cancellationToken: Ct);
            await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteAsync($"INSERT INTO {table} VALUES (1)", cancellationToken: Ct));
            await Assert.ThrowsAsync<VelQueryTransactionException>(async () => await session.BeginTransactionAsync(cancellationToken: Ct));
        }

        Assert.Equal(ConnectionState.Open, connection.State);
        await using (var command = connection.CreateCommand())
        {
            // Still the caller's open transaction: the first insert is visible to it.
            command.Transaction = transaction;
            command.CommandText = $"SELECT count(*) FROM {table}";
            Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
        }

        await transaction.RollbackAsync(Ct);
        await using var check = Session();
        Assert.Equal(0L, await check.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct));
    }

    [Fact]
    public async Task Caller_owned_connection_without_a_transaction_stays_open()
    {
        await using var connection = new SqliteConnection(Fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using (var session = Fixture.CreateDataSource().CreateSession(connection))
        {
            await using (var transaction = await session.BeginTransactionAsync(cancellationToken: Ct))
            {
                Assert.Equal(1L, await session.ExecuteScalarAsync<long>("SELECT 1", cancellationToken: Ct));
            }

            Assert.Equal(100L, await session.ExecuteScalarAsync<long>("SELECT count(*) FROM rows100", cancellationToken: Ct));
        }

        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task Dependency_injection_registers_a_singleton_data_source_and_scoped_sessions()
    {
        var services = new ServiceCollection();
        services.AddVelQuerySqlite(Fixture.ConnectionString, o => o.DefaultTimeout = TimeSpan.FromSeconds(5));

        var dataSource = Assert.Single(services, d => d.ServiceType == typeof(VelQueryDataSource));
        var session = Assert.Single(services, d => d.ServiceType == typeof(IVelQuerySession));
        Assert.Equal(ServiceLifetime.Singleton, dataSource.Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, session.Lifetime);

        await using var created = (VelQueryDataSource)dataSource.ImplementationFactory!(null!);
        Assert.Same(SqliteProvider.Instance, created.Provider);
        await using var scoped = created.CreateSession();
        Assert.Equal(2L, await scoped.ExecuteScalarAsync<long>("SELECT count(*) FROM victims", cancellationToken: Ct));
    }
}
