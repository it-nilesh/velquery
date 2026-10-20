using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace VelQuery.MongoDB.IntegrationTests;

/// <summary>
/// Ordered bulk insert. Outside a transaction MongoDB is not all-or-nothing: documents before the first failure stay
/// inserted. Inside a transaction nothing is committed after a failure.
/// </summary>
public sealed class BulkInsertTests(MongoFixture fixture) : IntegrationTest(fixture), IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await Fixture.Raw("accounts").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
        await Fixture.Raw("items").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IEnumerable<Account> AccountsWithDuplicateAt(int count, int duplicateIndex)
    {
        for (var i = 1; i <= count; i++)
        {
            yield return new Account(i == duplicateIndex ? 1 : i, "owner-" + i, i);
        }
    }

    [Fact]
    public async Task Inserts_a_large_lazy_sequence_and_reports_the_server_count()
    {
        await using var session = Session();
        var inserted = await InsertAccounts.ExecuteAsync(session, Enumerable.Range(1, 150_000).Select(i => new Account(i, "owner-" + (i % 10), i)), Ct);
        Assert.Equal(150_000, inserted);
        Assert.Equal(150_000, await CountAccounts.ExecuteAsync(session, Ct));
    }

    [Fact]
    public async Task Ordered_failure_without_a_transaction_keeps_the_documents_before_it()
    {
        await using var session = Session();
        var error = await Assert.ThrowsAsync<VelQueryProviderException>(() => InsertAccounts.ExecuteAsync(session, AccountsWithDuplicateAt(10, 6), Ct));
        var write = Assert.IsType<MongoWriteCommandException>(error.InnerException);
        var writeError = Assert.Single(write.WriteErrors);
        Assert.Equal(11000, writeError.Code);
        Assert.True(write.IsDuplicateKey);
        Assert.Equal(5, writeError.Index);
        Assert.Equal(5, write.Count); // Ordered: nothing after the failure was attempted.
        Assert.DoesNotContain("owner", write.Message, StringComparison.Ordinal);

        await using var check = Session();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, (await AllAccounts.ExecuteAsync(check, Ct)).Select(a => a.Id).ToArray());
    }

    [Fact]
    public async Task Ordered_failure_inside_a_transaction_commits_nothing()
    {
        await using (var session = Session())
        {
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
            var error = await Assert.ThrowsAsync<VelQueryProviderException>(() => InsertAccounts.ExecuteAsync(session, AccountsWithDuplicateAt(10, 6), Ct));
            Assert.IsType<MongoWriteCommandException>(error.InnerException);

            // The server aborted the transaction on the write error; committing is refused.
            await Assert.ThrowsAsync<VelQueryTransactionException>(() => transaction.CommitAsync(Ct));
        }

        await using var check = Session();
        Assert.Equal(0, await CountAccounts.ExecuteAsync(check, Ct));
    }

    [Fact]
    public async Task Bulk_insert_inside_a_committed_transaction_is_atomic()
    {
        await using (var session = Session())
        {
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
            Assert.Equal(1_000, await InsertAccounts.ExecuteAsync(session, Enumerable.Range(1, 1_000).Select(i => new Account(i, "t", i)), Ct));
            await using (var outside = Session())
            {
                Assert.Equal(0, await CountAccounts.ExecuteAsync(outside, Ct));
            }

            await transaction.CommitAsync(Ct);
        }

        await using var check = Session();
        Assert.Equal(1_000, await CountAccounts.ExecuteAsync(check, Ct));
    }

    [Fact]
    public async Task Empty_object_ids_are_assigned_by_the_server()
    {
        await using var session = Session();
        Assert.Equal(1_000, await InsertItems.ExecuteAsync(session, Enumerable.Range(0, 1_000).Select(i => new Item(ObjectId.Empty, "sku-" + i, i)), Ct));
        var items = await AllItems.ExecuteAsync(session, Ct);
        Assert.Equal(1_000, items.Select(i => i.Key).Where(k => k != ObjectId.Empty).Distinct().Count());
    }

    [Fact]
    public async Task Empty_sequence_inserts_nothing_without_a_round_trip()
    {
        using var source = Fixture.CreateDataSource();
        await using var session = source.CreateSession();
        Assert.Equal(0, await InsertAccounts.ExecuteAsync(session, [], Ct));
    }
}
