using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

/// <summary>
/// PRD §9.6/§13 pool hygiene: with a tiny pool, any leaked connection turns into a pool timeout on a later open.
/// After a long mix of successes, faults, timeouts, cancellations and early exits, the full pool must still be
/// available concurrently.
/// </summary>
public sealed class PoolHygieneTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    private const int MaxPool = 3;
    private const int Operations = 200;

    [Fact]
    public async Task Mixed_faulting_operations_do_not_leak_connections()
    {
        var tag = "hygiene";
        var connectionString = Fixture.ConnectionStringWith(b =>
        {
            b.MaxPoolSize = MaxPool;
            b.ConnectTimeout = 5;
            b.ApplicationName = "VelQuery.It." + tag + "." + Guid.NewGuid().ToString("N");
        });
        var dataSource = VelQuerySqlServer.CreateDataSource(connectionString, new VelQueryOptions { AllowDynamicFallback = true });
        var outcomes = new Dictionary<string, int>();

        for (var i = 0; i < Operations; i++)
        {
            await using var session = dataSource.CreateSession();
            var kind = await RunOneAsync(session, i);
            outcomes[kind] = outcomes.GetValueOrDefault(kind) + 1;
        }

        // Every kind of operation actually ran.
        Assert.Equal(Kinds, outcomes.Count);

        // All MaxPool connections must be obtainable at the same time: a single leak would make the last open time out.
        var held = new List<SqlConnection>();
        try
        {
            for (var i = 0; i < MaxPool; i++)
            {
                var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(Ct);
                held.Add(connection);
            }

            // ...and the server sees no more sessions for this pool than the pool allows.
            await using var probe = Fixture.CreateDataSource().CreateSession();
            var sessions = await probe.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = @App",
                new QueryParameters().Add("App", new SqlConnectionStringBuilder(connectionString).ApplicationName), Ct);
            Assert.InRange(sessions, 1, MaxPool);
        }
        finally
        {
            foreach (var connection in held)
            {
                await connection.DisposeAsync();
            }
        }

        // And VelQuery itself can still run MaxPool concurrent operations.
        var concurrent = new Task<int>[MaxPool];
        for (var i = 0; i < MaxPool; i++)
        {
            var session = dataSource.CreateSession();
            concurrent[i] = session.ExecuteScalarAsync<int>("WAITFOR DELAY '00:00:00.200'; SELECT 1", cancellationToken: Ct);
        }

        Assert.Equal(new[] { 1, 1, 1 }, await Task.WhenAll(concurrent));
    }

    private const int Kinds = 12;

    private static async Task<string> RunOneAsync(VelQuerySession session, int i)
    {
        switch (i % Kinds)
        {
            case 0:
                await session.QueryAsync(Descriptors.Rows100UpTo, 10L, Ct);
                return "buffered";
            case 1:
                await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteAsync("SELECT 1/0", cancellationToken: Ct));
                return "provider-error";
            case 2:
                await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => session.QuerySingleAsync(Descriptors.Rows100UpTo, 5L, Ct));
                return "too-many";
            case 3:
                await foreach (var _ in session.StreamAsync(Descriptors.Rows100kAll, default, Ct))
                {
                    break;
                }

                return "early-break";
            case 4:
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                    cts.CancelAfter(TimeSpan.FromMilliseconds(50));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => session.ExecuteAsync("WAITFOR DELAY '00:00:05'", cancellationToken: cts.Token));
                    return "cancelled";
                }

            case 5:
                await Assert.ThrowsAsync<VelQueryMappingException>(
                    () => session.QueryAsync<IdCategory>("SELECT Id, Category FROM dbo.Rows100 ORDER BY Id", cancellationToken: Ct));
                return "mapping-error";
            case 6:
                {
                    await using var tx = await session.BeginTransactionAsync(cancellationToken: Ct);
                    await session.ExecuteAsync("INSERT dbo.Scratch (Marker) VALUES (N'pool-rollback')", cancellationToken: Ct);

                    // Disposed without commit.
                    return "tx-rollback";
                }

            case 7:
                {
                    await using var reader = await session.QueryMultipleAsync("SELECT 1; SELECT 2; SELECT 3;", cancellationToken: Ct);
                    await reader.ReadAsync<int>(Ct);

                    // Abandoned with results outstanding.
                    return "multi-abandoned";
                }

            case 8:
                await Assert.ThrowsAsync<VelQueryProviderException>(
                    () => session.QueryAsync<long>("SELECT Id FROM dbo.Rows1; SELECT 1/0;", cancellationToken: Ct));
                return "batch-error";
            case 9:
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                    var seen = 0;
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    {
                        await foreach (var _ in session.StreamAsync<IdName>("SELECT a.Id, a.Name FROM dbo.Rows100k a CROSS JOIN dbo.Rows100 b", cancellationToken: cts.Token))
                        {
                            if (++seen == 100)
                            {
                                await cts.CancelAsync();
                            }
                        }
                    });
                    return "stream-cancelled";
                }

            case 10:
                await Assert.ThrowsAsync<VelQueryTimeoutException>(
                    () => session.Sql("WAITFOR DELAY '00:00:03'").WithTimeout(TimeSpan.FromSeconds(1)).ExecuteAsync(Ct));
                return "timeout";
            default:
                await Assert.ThrowsAsync<VelQueryProviderException>(
                    () => session.ExecuteAsync("SELECT * FROM dbo.DoesNotExist", cancellationToken: Ct));
                return "missing-object";
        }
    }
}
