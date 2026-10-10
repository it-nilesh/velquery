using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace VelQuery.PostgreSQL.IntegrationTests;

/// <summary>Timeouts, cancellation, streaming, pool hygiene and transaction ownership on PostgreSQL.</summary>
public sealed class LifecycleTests(PostgreSqlFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Command_timeout_is_reported_as_timeout_with_the_provider_exception()
    {
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<VelQueryTimeoutException>(() =>
            session.Sql("SELECT pg_sleep(5)").WithTimeout(TimeSpan.FromSeconds(1)).ExecuteAsync(Ct));

        Assert.IsAssignableFrom<NpgsqlException>(ex.InnerException);
        Assert.Equal(1, await session.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
    }

    [Fact]
    public async Task Caller_cancellation_stops_a_running_query_quickly()
    {
        await using var session = Session();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync("SELECT pg_sleep(10)", cancellationToken: cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"cancellation took {stopwatch.Elapsed}");
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

            Assert.Equal(PostgreSqlFixture.LargeRowCount, count);

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
        await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
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
