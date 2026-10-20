using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace VelQuery.MongoDB.IntegrationTests;

/// <summary>Multi-document transactions on VelQuery-owned driver sessions, and application-owned sessions (VQ-FR-011/012).</summary>
public sealed class TransactionTests(MongoFixture fixture) : IntegrationTest(fixture), IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        var accounts = Fixture.Raw("accounts");
        await accounts.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
        await using var session = Session();
        await InsertAccounts.ExecuteAsync(session, [new Account(1, "alice", 100m), new Account(2, "bob", 50m)], Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Commit_makes_every_write_visible_atomically()
    {
        await using (var session = Session())
        {
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
            Assert.Same(transaction.ClientSession, session.ClientSession);
            await Deposit.ExecuteAsync(session, 1, -30m, Ct);
            await Deposit.ExecuteAsync(session, 2, 30m, Ct);
            await InsertAccount.ExecuteAsync(session, new Account(3, "carol", 0m), Ct);

            // Reads inside the transaction see its writes; readers outside do not.
            Assert.Equal(3, await CountAccounts.ExecuteAsync(session, Ct));
            await using (var outside = Session())
            {
                Assert.Equal(2, await CountAccounts.ExecuteAsync(outside, Ct));
            }

            await transaction.CommitAsync(Ct);
            Assert.True(transaction.IsCompleted);
            Assert.Null(session.ClientSession);
        }

        await using var check = Session();
        var accounts = await AllAccounts.ExecuteAsync(check, Ct);
        Assert.Equal([70m, 80m, 0m], accounts.Select(a => a.Balance).ToArray());
    }

    [Fact]
    public async Task Disposing_without_commit_aborts()
    {
        await using (var session = Session())
        {
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
            await Deposit.ExecuteAsync(session, 1, 1_000m, Ct);
            await InsertAccount.ExecuteAsync(session, new Account(4, "dave", 1m), Ct);
        }

        await using var check = Session();
        var accounts = await AllAccounts.ExecuteAsync(check, Ct);
        Assert.Equal([1, 2], accounts.Select(a => a.Id).ToArray());
        Assert.Equal(100m, accounts[0].Balance);
    }

    [Fact]
    public async Task Disposing_the_session_aborts_an_active_transaction()
    {
        var session = Session();
        _ = await session.BeginTransactionAsync(cancellationToken: Ct);
        await Deposit.ExecuteAsync(session, 1, 1_000m, Ct);
        await session.DisposeAsync();

        await using var check = Session();
        Assert.Equal(100m, (await AllAccounts.ExecuteAsync(check, Ct))[0].Balance);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => CountAccounts.ExecuteAsync(session, Ct));
    }

    [Fact]
    public async Task Explicit_rollback_discards_and_completes_the_scope()
    {
        await using var session = Session();
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
        await DeleteAccount.ExecuteAsync(session, 1, Ct);
        await transaction.RollbackAsync(Ct);

        await Assert.ThrowsAsync<VelQueryTransactionException>(() => transaction.CommitAsync(Ct));
        Assert.Equal(2, await CountAccounts.ExecuteAsync(session, Ct)); // Runs outside the completed transaction.
    }

    [Fact]
    public async Task Overlapping_operations_on_the_transaction_session_fail_fast()
    {
        await using var session = Session();
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
        await using (var stream = StreamAccounts.ExecuteAsync(session, Ct).GetAsyncEnumerator(Ct))
        {
            Assert.True(await stream.MoveNextAsync());
            var overlap = await Assert.ThrowsAsync<VelQueryValidationException>(() => CountAccounts.ExecuteAsync(session, Ct));
            Assert.Contains("not thread-safe", overlap.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<VelQueryValidationException>(() => transaction.CommitAsync(Ct));
        }

        // The guard is released when the stream is disposed.
        Assert.Equal(2, await CountAccounts.ExecuteAsync(session, Ct));
    }

    [Fact]
    public async Task A_second_transaction_requires_disposing_the_first()
    {
        await using var session = Session();
        var first = await session.BeginTransactionAsync(cancellationToken: Ct);
        await Assert.ThrowsAsync<VelQueryTransactionException>(() => session.BeginTransactionAsync(cancellationToken: Ct));
        await first.DisposeAsync();
        await using var second = await session.BeginTransactionAsync(cancellationToken: Ct);
        await second.CommitAsync(Ct);
    }

    [Fact]
    public async Task Application_owned_client_session_is_used_but_never_completed_or_ended()
    {
        var client = DataSource.Client;
        using var handle = await client.StartSessionAsync(cancellationToken: Ct);
        handle.StartTransaction();
        await using (var session = DataSource.CreateSession(handle))
        {
            Assert.Same(handle, session.ClientSession);
            await Deposit.ExecuteAsync(session, 2, 5m, Ct);
            await Assert.ThrowsAsync<VelQueryTransactionException>(() => session.BeginTransactionAsync(cancellationToken: Ct));
        }

        // VelQuery neither committed nor aborted nor ended the application's session.
        Assert.True(handle.IsInTransaction);
        await handle.AbortTransactionAsync(Ct);

        await using var check = Session();
        Assert.Equal(50m, (await AllAccounts.ExecuteAsync(check, Ct))[1].Balance);

        // The handle is still alive and usable by the application.
        await client.GetDatabase(MongoFixture.Database).RunCommandAsync<BsonDocument>(handle, new BsonDocument("ping", 1), cancellationToken: Ct);
    }

    [Fact]
    public async Task A_client_session_from_another_client_is_rejected()
    {
        using var other = new MongoClient(Fixture.ConnectionString);
        using var handle = await other.StartSessionAsync(cancellationToken: Ct);
        Assert.Throws<VelQueryValidationException>(() => DataSource.CreateSession(handle));
    }
}
