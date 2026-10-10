using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Xunit;

namespace VelQuery.SqlServer.IntegrationTests;

/// <summary>VQ-FR-001, VQ-FR-011, VQ-FR-012, PRD §18.1 caller-transaction adversarial case.</summary>
public sealed class TransactionTests(SqlServerFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Session_transaction_commit_persists()
    {
        var marker = NewMarker();
        await using (var session = Session())
        {
            await using var tx = await session.BeginTransactionAsync(cancellationToken: Ct);
            Assert.Same(tx.DbTransaction, session.Transaction);
            await InsertAsync(session, marker, 2);
            Assert.Equal(2, await CountAsync(session, marker));
            await tx.CommitAsync(Ct);
        }

        Assert.Equal(2, await CountFromOtherSessionAsync(marker));
    }

    [Fact]
    public async Task Session_transaction_disposed_without_commit_rolls_back()
    {
        var marker = NewMarker();
        await using (var session = Session())
        {
            await using var tx = await session.BeginTransactionAsync(cancellationToken: Ct);
            await InsertAsync(session, marker, 3);
            Assert.Equal(3, await CountAsync(session, marker));
        }

        Assert.Equal(0, await CountFromOtherSessionAsync(marker));
    }

    [Fact]
    public async Task Session_disposed_with_open_transaction_rolls_back()
    {
        var marker = NewMarker();
        var session = Session();
        _ = await session.BeginTransactionAsync(cancellationToken: Ct);
        await InsertAsync(session, marker, 1);
        await session.DisposeAsync();

        Assert.Equal(0, await CountFromOtherSessionAsync(marker));
    }

    [Fact]
    public async Task Session_transaction_explicit_rollback_and_reuse()
    {
        var marker = NewMarker();
        await using var session = Session();
        await using (var tx = await session.BeginTransactionAsync(IsolationLevel.Serializable, Ct))
        {
            await InsertAsync(session, marker, 1);
            await tx.RollbackAsync(Ct);
            await Assert.ThrowsAsync<VelQueryTransactionException>(() => tx.CommitAsync(Ct));
        }

        // After the scope ends, the session acquires pooled connections per operation again and can start a new one.
        Assert.Null(session.Transaction);
        Assert.Equal(0, await CountAsync(session, marker));
        await using var second = await session.BeginTransactionAsync(cancellationToken: Ct);
        await InsertAsync(session, marker, 1);
        await second.CommitAsync(Ct);
        Assert.Equal(1, await CountFromOtherSessionAsync(marker));
    }

    [Fact]
    public async Task Session_transaction_isolation_level_is_applied()
    {
        await using var session = Session();
        await using var tx = await session.BeginTransactionAsync(IsolationLevel.Serializable, Ct);
        var level = await session.ExecuteScalarAsync<short>(
            "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID", cancellationToken: Ct);
        Assert.Equal((short)4, level); // 4 = SERIALIZABLE
    }

    [Fact]
    public async Task Failed_statement_inside_session_transaction_leaves_rollback_to_scope()
    {
        var marker = NewMarker();
        await using (var session = Session())
        {
            await using var tx = await session.BeginTransactionAsync(cancellationToken: Ct);
            await InsertAsync(session, marker, 1);
            await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteAsync("SELECT 1/0", cancellationToken: Ct));

            // The pinned connection is still usable inside the transaction after a provider error.
            Assert.Equal(1, await CountAsync(session, marker));
        }

        Assert.Equal(0, await CountFromOtherSessionAsync(marker));
    }

    [Fact]
    public async Task Caller_owned_transaction_is_used_and_never_committed()
    {
        var marker = NewMarker();
        await using var connection = await Fixture.OpenConnectionAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Ct);

        await using (var session = DataSource().CreateSession(connection, transaction))
        {
            Assert.Same(transaction, session.Transaction);
            await InsertAsync(session, marker, 2);
            Assert.Equal(2, await CountAsync(session, marker));
            await Assert.ThrowsAsync<VelQueryTransactionException>(async () => await session.BeginTransactionAsync(cancellationToken: Ct));
        }

        // Session disposal touched neither the connection nor the transaction.
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);
        Assert.Equal(1, await TranCountAsync(connection, transaction));
        Assert.Equal(0, await CountFromOtherSessionReadUncommittedOnlyCommittedAsync(marker));

        transaction.Rollback();
        Assert.Equal(0, await CountFromOtherSessionAsync(marker));
    }

    [Fact]
    public async Task Caller_owned_transaction_commit_is_the_callers_decision()
    {
        var marker = NewMarker();
        await using var connection = await Fixture.OpenConnectionAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Ct);
        await using (var session = DataSource().CreateSession(connection, transaction))
        {
            await InsertAsync(session, marker, 1);
        }

        await transaction.CommitAsync(Ct);
        Assert.Equal(1, await CountFromOtherSessionAsync(marker));
    }

    [Fact]
    public async Task Failing_query_inside_caller_transaction_leaves_transaction_to_caller()
    {
        var marker = NewMarker();
        await using var connection = await Fixture.OpenConnectionAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Ct);
        await using (var session = DataSource().CreateSession(connection, transaction))
        {
            await InsertAsync(session, marker, 1);
            var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.QueryAsync<int>("SELECT 1/0", cancellationToken: Ct));
            Assert.Equal(8134, Assert.IsType<SqlException>(ex.InnerException).Number);

            await Assert.ThrowsAsync<VelQueryMappingException>(
                () => session.QueryAsync<IdCategory>("SELECT Id, Category FROM dbo.Rows100 WHERE Id = 10", cancellationToken: Ct));
        }

        // Neither committed nor rolled back by VelQuery: still open, work still pending.
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);
        Assert.Equal(1, await TranCountAsync(connection, transaction));

        // The caller may still commit the work done before the failure.
        await transaction.CommitAsync(Ct);
        Assert.Equal(1, await CountFromOtherSessionAsync(marker));
    }

    [Fact]
    public async Task Caller_owned_connection_without_transaction_is_never_closed()
    {
        await using var connection = await Fixture.OpenConnectionAsync();
        await using (var session = DataSource().CreateSession(connection))
        {
            Assert.Equal(1, await session.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
            await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteAsync("SELECT 1/0", cancellationToken: Ct));
            await foreach (var _ in session.StreamAsync(Descriptors.Rows100kAll, default, Ct))
            {
                break;
            }

            Assert.Equal(1, await session.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
        }

        Assert.Equal(ConnectionState.Open, connection.State);
        await using var command = new SqlCommand("SELECT 2", connection);
        Assert.Equal(2, await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Pinned_session_rejects_overlapping_operations()
    {
        await using var connection = await Fixture.OpenConnectionAsync();
        await using var session = DataSource().CreateSession(connection);
        await using var enumerator = session.StreamAsync(Descriptors.Rows100UpTo, 10L, Ct).GetAsyncEnumerator(Ct);
        Assert.True(await enumerator.MoveNextAsync());

        await Assert.ThrowsAsync<VelQueryValidationException>(() => session.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: Ct));
    }

    private static string NewMarker() => "tx-" + Guid.NewGuid().ToString("N");

    private static Task<int> InsertAsync(VelQuerySession session, string marker, int rows)
    {
        var values = string.Join(", ", System.Linq.Enumerable.Repeat("(@Marker)", rows));
        return session.ExecuteAsync($"INSERT dbo.Scratch (Marker) VALUES {values}", new QueryParameters().Add("Marker", marker), Ct);
    }

    private static Task<int> CountAsync(VelQuerySession session, string marker)
        => session.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Scratch WHERE Marker = @Marker", new QueryParameters().Add("Marker", marker), Ct);

    private async Task<int> CountFromOtherSessionAsync(string marker)
    {
        await using var other = Session();
        return await CountAsync(other, marker);
    }

    /// <summary>Counts committed rows without blocking on the open transaction's locks (READPAST skips them).</summary>
    private async Task<int> CountFromOtherSessionReadUncommittedOnlyCommittedAsync(string marker)
    {
        await using var other = Session();
        return await other.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Scratch WITH (READPAST) WHERE Marker = @Marker", new QueryParameters().Add("Marker", marker), Ct);
    }

    private static async Task<int> TranCountAsync(SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = new SqlCommand("SELECT @@TRANCOUNT", connection, transaction);
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }
}
