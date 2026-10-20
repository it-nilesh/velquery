using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace VelQuery.MongoDB.IntegrationTests;

/// <summary>Writes return exactly the counts the server reports; driver failures stay inspectable (AGENTS rules 3, 5).</summary>
public sealed class WriteTests(MongoFixture fixture) : IntegrationTest(fixture), IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await Fixture.Raw("accounts").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);
        await using var session = Session();
        await InsertAccounts.ExecuteAsync(session,
            [new Account(1, "alice", 10m), new Account(2, "bob", 20m), new Account(3, "bob", 30m), new Account(4, "carol", 40m)], Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Update_one_reports_matched_and_modified()
    {
        await using var session = Session();
        Assert.Equal(new VelQueryMongoUpdateResult(1, 1, null), await Deposit.ExecuteAsync(session, 1, 5m, Ct));
        Assert.Equal(new VelQueryMongoUpdateResult(0, 0, null), await Deposit.ExecuteAsync(session, 99, 5m, Ct));
        Assert.Equal(15m, (await AllAccounts.ExecuteAsync(session, Ct))[0].Balance);
    }

    [Fact]
    public async Task Update_many_reports_server_counts_including_no_op_matches()
    {
        await using var session = Session();
        Assert.Equal(new VelQueryMongoUpdateResult(2, 2, null), await RenameOwner.ExecuteAsync(session, "bob", "robert", Ct));

        // $set to the current value matches but does not modify: the server says so and VelQuery does not "fix" it.
        Assert.Equal(new VelQueryMongoUpdateResult(1, 0, null), await RenameOwner.ExecuteAsync(session, "alice", "alice", Ct));
    }

    [Fact]
    public async Task Upsert_reports_the_upserted_id()
    {
        await using var session = Session();
        var inserted = await UpsertAccount.ExecuteAsync(session, 50, "erin", 1m, Ct);
        Assert.Equal(0, inserted.MatchedCount);
        Assert.Equal(0, inserted.ModifiedCount);
        Assert.Equal(new BsonInt32(50), inserted.UpsertedId);

        var updated = await UpsertAccount.ExecuteAsync(session, 50, "erin", 2m, Ct);
        Assert.Equal(new VelQueryMongoUpdateResult(1, 1, null), updated);
    }

    [Fact]
    public async Task Delete_reports_deleted_counts()
    {
        await using var session = Session();
        Assert.Equal(1, await DeleteAccount.ExecuteAsync(session, 1, Ct));
        Assert.Equal(0, await DeleteAccount.ExecuteAsync(session, 1, Ct));
        Assert.Equal(2, await DeleteByOwner.ExecuteAsync(session, "bob", Ct));
        Assert.Equal(1, await CountAccounts.ExecuteAsync(session, Ct));
    }

    [Fact]
    public async Task Duplicate_key_is_a_provider_exception_with_the_server_error_inspectable()
    {
        await using var session = Session();
        var error = await Assert.ThrowsAsync<VelQueryProviderException>(() => InsertAccount.ExecuteAsync(session, new Account(1, "secret-owner-name", 1m), Ct));
        Assert.Equal("accounts.insert", error.QueryId);
        Assert.Equal(VelQueryErrorCategory.Provider, error.Category);
        var write = Assert.IsType<MongoWriteException>(error.InnerException);
        Assert.Equal(ServerErrorCategory.DuplicateKey, write.WriteError.Category);
        Assert.Equal(11000, write.WriteError.Code);

        // VelQuery's own message carries no values (the driver's inner message is the driver's business).
        Assert.DoesNotContain("secret-owner-name", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1", error.Message.Replace("accounts.insert", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Count_returning_writes_are_refused_under_unacknowledged_write_concern()
    {
        using var source = Fixture.CreateDataSource(s => s.WriteConcern = WriteConcern.Unacknowledged);
        await using var session = source.CreateSession();
        var error = await Assert.ThrowsAsync<VelQueryUnsupportedCapabilityException>(() => DeleteByOwner.ExecuteAsync(session, "bob", Ct));
        Assert.Equal("accounts.deleteByOwner", error.QueryId);
        await Assert.ThrowsAsync<VelQueryUnsupportedCapabilityException>(() => Deposit.ExecuteAsync(session, 1, 1m, Ct));
        await Assert.ThrowsAsync<VelQueryUnsupportedCapabilityException>(() => InsertAccounts.ExecuteAsync(session, [new Account(70, "x", 0m)], Ct));

        // Nothing was sent.
        await using var check = Session();
        Assert.Equal(4, await CountAccounts.ExecuteAsync(check, Ct));
        Assert.Equal(10m, (await AllAccounts.ExecuteAsync(check, Ct))[0].Balance);
    }

    [Fact]
    public async Task Null_document_is_rejected()
    {
        await using var session = Session();
        await Assert.ThrowsAsync<ArgumentNullException>(() => InsertAccount.ExecuteAsync(session, null!, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => InsertAccounts.ExecuteAsync(session, [new Account(80, "x", 0m), null!], Ct));
    }
}
