using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace VelQuery.MongoDB.IntegrationTests;

/// <summary>
/// Writes run as raw insert/update/delete commands: the server enforces the operation timeout (maxTimeMS) and bulk
/// inserts are split by the server's size and count limits.
/// </summary>
public sealed class WriteCommandTests(MongoFixture fixture) : IntegrationTest(fixture), IAsyncLifetime
{
    // $size of the 20,000-element range is never greater than this, so the slow filters match nothing.
    private const int NeverMatches = 1_000_000;

    public async ValueTask InitializeAsync()
    {
        await Fixture.Raw("blobs").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
        await Fixture.Raw("accounts").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Update_timeout_is_enforced_by_the_server()
    {
        await using var session = Session();
        var stopwatch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<VelQueryTimeoutException>(() => SlowUpdate.ExecuteAsync(session, NeverMatches, Ct));

        // The server stopped it (MaxTimeMSExpired), not the client deadline one grace period later.
        Assert.IsType<MongoExecutionTimeoutException>(error.InnerException);
        Assert.Equal("big.slow.update", error.QueryId);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1.2), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Delete_timeout_is_enforced_by_the_server()
    {
        await using var session = Session();

        var error = await Assert.ThrowsAsync<VelQueryTimeoutException>(() => SlowDelete.ExecuteAsync(session, NeverMatches, Ct));

        Assert.IsType<MongoExecutionTimeoutException>(error.InnerException);
    }

    [Fact]
    public async Task Bulk_insert_splits_large_documents_into_several_commands_by_size()
    {
        await using var session = Session();
        var payload = new string('x', 1024 * 1024);

        // 40 documents of ~1 MiB exceed one 16 MiB command several times over.
        var inserted = await InsertBlobs.ExecuteAsync(session, Enumerable.Range(1, 40).Select(i => new Blob(i, payload)), Ct);

        Assert.Equal(40, inserted);
        Assert.Equal(40, await CountBlobs.ExecuteAsync(session, Ct));
    }

    [Fact]
    public async Task Bulk_insert_failure_in_a_later_batch_reports_its_index_across_the_sequence()
    {
        await using var session = Session();
        const int duplicateAt = 120_000; // second batch (the server's batch limit is 100,000 documents)
        var accounts = Enumerable.Range(0, 150_000).Select(i => new Account(i == duplicateAt ? 1 : i + 1, "o", i));

        var error = await Assert.ThrowsAsync<VelQueryProviderException>(() => InsertAccounts.ExecuteAsync(session, accounts, Ct));

        var write = Assert.IsType<MongoWriteCommandException>(error.InnerException);
        Assert.True(write.IsDuplicateKey);
        Assert.Equal(duplicateAt, write.WriteErrors[0].Index);
        Assert.Equal(duplicateAt, write.Count);
        Assert.Equal(duplicateAt, await CountAccounts.ExecuteAsync(session, Ct));
    }

    [Fact]
    public async Task Upsert_reports_the_upserted_id_and_does_not_count_it_as_matched()
    {
        await using var session = Session();

        var created = await UpsertAccount.ExecuteAsync(session, 7, "ann", 10m, Ct);
        var updated = await UpsertAccount.ExecuteAsync(session, 7, "ann", 20m, Ct);

        Assert.Equal(new VelQueryMongoUpdateResult(0, 0, new BsonInt32(7)), created);
        Assert.Equal(new VelQueryMongoUpdateResult(1, 1, null), updated);
    }

    [Fact]
    public async Task Writes_inside_a_transaction_commit_or_roll_back_together()
    {
        await using (var session = Session())
        {
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: Ct);
            Assert.Equal(3, await InsertAccounts.ExecuteAsync(session, [new(1, "a", 1), new(2, "b", 2), new(3, "c", 3)], Ct));
            Assert.Equal(new VelQueryMongoUpdateResult(1, 1, null), await Deposit.ExecuteAsync(session, 1, 5m, Ct));
            Assert.Equal(1, await DeleteAccount.ExecuteAsync(session, 3, Ct));
        }

        await using var check = Session();
        Assert.Equal(0, await CountAccounts.ExecuteAsync(check, Ct));
    }
}
