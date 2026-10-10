using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>VQ-FR-011 / VQ-FR-012: session transactions pin one connection; caller transactions are never completed.</summary>
public sealed class TransactionTests
{
    private const string Sql = "select v from t";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Harness Create()
    {
        var h = new Harness();
        h.Script(Sql, FakeResultSet.Ints("v", 2)).RecordsAffected = 2;
        return h;
    }

    [Fact]
    public async Task Factory_session_transaction_pins_one_connection_for_every_operation()
    {
        var h = Create();
        await using var session = h.Session();

        await using (var tx = await session.BeginTransactionAsync(IsolationLevel.Serializable, Ct))
        {
            Assert.Same(tx.DbTransaction, session.Transaction);
            Assert.Equal(IsolationLevel.Serializable, tx.DbTransaction.IsolationLevel);

            await session.QueryAsync<int>(Sql, null, Ct);
            await session.ExecuteAsync(Sql, null, Ct);
            await session.ExecuteScalarAsync<int>(Sql, null, Ct);
            await session.QueryAsync(Descriptors.Scalar<int>(Sql), new NoArgs(), Ct);
            await foreach (var _ in session.StreamAsync<int>(Sql, null, Ct))
            {
            }

            await using (var multi = await session.QueryMultipleAsync(Sql, null, Ct))
            {
                await multi.ReadAsync<int>(Ct);
            }

            var connection = Assert.Single(h.Db.Connections);
            Assert.Equal(0, connection.DisposeCount);
            Assert.Equal(6, h.Db.Commands.Count);
            Assert.All(h.Db.Commands, c =>
            {
                Assert.Same(connection, c.Connection);
                Assert.Same(tx.DbTransaction, c.TransactionAtExecute);
                Assert.Equal(1, c.DisposeCount);
            });

            await tx.CommitAsync(Ct);
        }

        var fake = Assert.Single(h.Db.Transactions);
        Assert.Equal(1, fake.CommitCount);
        Assert.False(fake.WasRolledBack);
        Assert.Equal(1, Assert.Single(h.Db.Connections).DisposeCount);
        Assert.Null(session.Transaction);
    }

    [Fact]
    public async Task Disposing_without_commit_rolls_back_and_releases_the_connection()
    {
        var h = Create();
        await using var session = h.Session();

        await using (var tx = await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            await session.ExecuteAsync(Sql, null, Ct);
        }

        var fake = Assert.Single(h.Db.Transactions);
        Assert.True(fake.WasRolledBack);
        Assert.Equal(0, fake.CommitCount);
        Assert.Equal(1, Assert.Single(h.Db.Connections).DisposeCount);
    }

    [Fact]
    public async Task Failure_inside_scope_then_dispose_rolls_back()
    {
        var h = Create();
        h.Db.Script("bad", new FakeScript { ExecuteFault = new FakeDbException() });
        await using var session = h.Session();

        await using (await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            await session.ExecuteAsync(Sql, null, Ct);
            await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteAsync("bad", null, Ct));
            Assert.Equal(0, Assert.Single(h.Db.Connections).DisposeCount);
        }

        Assert.True(Assert.Single(h.Db.Transactions).WasRolledBack);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Explicit_rollback_then_dispose_does_not_roll_back_twice()
    {
        var h = Create();
        await using var session = h.Session();

        await using (var tx = await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            await tx.RollbackAsync(Ct);
            await Assert.ThrowsAsync<VelQueryTransactionException>(() => tx.CommitAsync(Ct));
        }

        var fake = Assert.Single(h.Db.Transactions);
        Assert.Equal(1, fake.RollbackCount);
        Assert.False(fake.RolledBackOnDispose);
    }

    [Fact]
    public async Task Commit_then_dispose_does_not_roll_back()
    {
        var h = Create();
        await using var session = h.Session();

        var tx = await session.BeginTransactionAsync(cancellationToken: Ct);
        await tx.CommitAsync(Ct);
        await tx.DisposeAsync();
        await tx.DisposeAsync();

        var fake = Assert.Single(h.Db.Transactions);
        Assert.Equal(1, fake.CommitCount);
        Assert.False(fake.WasRolledBack);
        Assert.Equal(1, fake.DisposeCount);
        Assert.Equal(1, Assert.Single(h.Db.Connections).DisposeCount);
    }

    [Fact]
    public async Task Failed_commit_is_reported_with_the_provider_exception_and_scope_still_releases()
    {
        var h = Create();
        await using var session = h.Session();
        var fault = new FakeDbException("commit failed");

        await using (var tx = await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            ((FakeDbTransaction)tx.DbTransaction).CommitFault = fault;
            var ex = await Assert.ThrowsAsync<VelQueryTransactionException>(() => tx.CommitAsync(Ct));
            Assert.Same(fault, ex.InnerException);
            Assert.Equal(VelQueryErrorCategory.Transaction, ex.Category);
        }

        Assert.Equal(1, Assert.Single(h.Db.Connections).DisposeCount);
    }

    [Fact]
    public async Task Nested_begin_throws_transaction_exception()
    {
        var h = Create();
        await using var session = h.Session();
        await using var tx = await session.BeginTransactionAsync(cancellationToken: Ct);

        var ex = await Assert.ThrowsAsync<VelQueryTransactionException>(() => session.BeginTransactionAsync(cancellationToken: Ct).AsTask());

        Assert.Equal(VelQueryErrorCategory.Transaction, ex.Category);
        Assert.Single(h.Db.Connections);
        Assert.Single(h.Db.Transactions);

        // The outer scope is still usable.
        await session.ExecuteAsync(Sql, null, Ct);
    }

    [Fact]
    public async Task Begin_on_caller_transaction_session_throws_and_leaves_caller_transaction_untouched()
    {
        var h = Create();
        using var connection = h.Db.OpenCallerConnection();
        using var callerTx = connection.BeginTransaction();
        await using (var session = h.DataSource.CreateSession(connection, callerTx))
        {
            await Assert.ThrowsAsync<VelQueryTransactionException>(() => session.BeginTransactionAsync(cancellationToken: Ct).AsTask());
            await session.ExecuteAsync(Sql, null, Ct);
        }

        var fake = Assert.Single(h.Db.Transactions);
        Assert.Equal(0, fake.CommitCount);
        Assert.False(fake.WasRolledBack);
        Assert.Equal(0, fake.DisposeCount);
        Assert.Equal(0, connection.DisposeCount);
    }

    [Fact]
    public async Task Session_transaction_on_caller_connection_rolls_back_but_keeps_the_connection()
    {
        var h = Create();
        using var connection = h.Db.OpenCallerConnection();
        await using (var session = h.DataSource.CreateSession(connection))
        {
            await using (var tx = await session.BeginTransactionAsync(cancellationToken: Ct))
            {
                await session.ExecuteAsync(Sql, null, Ct);
                Assert.Same(tx.DbTransaction, Assert.Single(h.Db.Commands).TransactionAtExecute);
            }

            await session.ExecuteAsync(Sql, null, Ct);
        }

        Assert.True(Assert.Single(h.Db.Transactions).WasRolledBack);
        Assert.Null(h.Db.Commands[1].TransactionAtExecute);
        Assert.Equal(0, connection.DisposeCount);
        Assert.Equal(0, connection.CloseCount);
    }

    [Fact]
    public async Task After_scope_disposal_operations_use_fresh_connections_without_a_transaction()
    {
        var h = Create();
        await using var session = h.Session();

        await using (await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            await session.ExecuteAsync(Sql, null, Ct);
        }

        await session.ExecuteAsync(Sql, null, Ct);
        await session.ExecuteAsync(Sql, null, Ct);

        Assert.Equal(3, h.Db.Connections.Count);
        Assert.All(h.Db.Commands.Skip(1), c => Assert.Null(c.TransactionAtExecute));
        h.Db.AssertAllReleasedExactlyOnce();

        // A new transaction can be started after the previous one ended.
        await using (var tx = await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            await tx.CommitAsync(Ct);
        }

        Assert.Equal(2, h.Db.Transactions.Count);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Disposing_the_session_rolls_back_an_open_scope_and_releases_the_connection()
    {
        var h = Create();
        var session = h.Session();
        _ = await session.BeginTransactionAsync(cancellationToken: Ct);
        await session.ExecuteAsync(Sql, null, Ct);

        await session.DisposeAsync();

        Assert.True(Assert.Single(h.Db.Transactions).WasRolledBack);
        h.Db.AssertAllReleasedExactlyOnce();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteAsync(Sql, null, Ct));
    }

    [Fact]
    public async Task Begin_failure_releases_the_connection_and_wraps_provider_errors()
    {
        var h = Create();
        await using var session = h.Session();
        var fault = new FakeDbException("open failed");
        h.Db.ConnectionOpenFault = fault;

        var ex = await Assert.ThrowsAsync<VelQueryTransactionException>(() => session.BeginTransactionAsync(cancellationToken: Ct).AsTask());

        Assert.Same(fault, ex.InnerException);
        Assert.Equal(1, Assert.Single(h.Db.Connections).DisposeCount);

        // Not left pinned or busy.
        h.Db.ConnectionOpenFault = null;
        await session.ExecuteAsync(Sql, null, Ct);
        h.Db.AssertAllReleasedExactlyOnce();
    }

    [Fact]
    public async Task Begin_cancelled_during_connect_releases_the_connection()
    {
        var h = Create();
        await using var session = h.Session();
        using var cts = new CancellationTokenSource();
        h.Db.OnConnectionOpen = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.BeginTransactionAsync(cancellationToken: cts.Token).AsTask());

        Assert.Equal(1, Assert.Single(h.Db.Connections).DisposeCount);
        Assert.Empty(h.Db.Transactions);
    }
}
