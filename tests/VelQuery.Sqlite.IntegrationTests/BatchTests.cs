using System;
using System.Threading.Tasks;
using Xunit;

namespace VelQuery.Sqlite.IntegrationTests;

/// <summary>VelQueryBatch on Sqlite: commands run one command at a time on one connection (Microsoft.Data.Sqlite has no DbBatch).</summary>
public sealed class BatchTests(SqliteFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Batch_runs_every_command_and_reports_each_affected_count()
    {
        var table = await CreateTableAsync("id int PRIMARY KEY, name varchar(20) NOT NULL");
        await using var session = Session();

        var counts = await session.CreateBatch("batch.import")
            .Add($"INSERT INTO {table} (id, name) VALUES (@id, @name)", new QueryParameters().Add("id", 1).Add("name", "a"))
            .Add($"INSERT INTO {table} (id, name) VALUES (2, 'b'), (3, 'c')")
            .Add($"UPDATE {table} SET name = 'u' WHERE id >= @min", new QueryParameters().Add("min", 2))
            .ExecuteAsync(Ct);

        Assert.Equal([1, 2, 2], counts);
        Assert.Equal(3L, await session.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}", cancellationToken: Ct));
    }

    [Fact]
    public async Task A_failing_command_is_named_and_a_transaction_makes_the_batch_atomic()
    {
        var table = await CreateTableAsync("id int PRIMARY KEY, name varchar(20) NOT NULL");
        await using var session = Session();
        await using (var transaction = await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            var batch = session.CreateBatch("batch.atomic")
                .Add($"INSERT INTO {table} (id, name) VALUES (1, 'a')", queryId: "batch.first")
                .Add($"INSERT INTO {table} (id, name) VALUES (1, 'leak-check')", queryId: "batch.duplicate")
                .Add($"INSERT INTO {table} (id, name) VALUES (2, 'b')", queryId: "batch.last");

            var error = await Assert.ThrowsAnyAsync<VelQueryException>(() => batch.ExecuteAsync(Ct));
            Assert.Equal("batch.duplicate", error.QueryId);
            Assert.DoesNotContain("leak-check", error.Message, StringComparison.Ordinal); // no SQL or values in messages
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(0L, await session.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}", cancellationToken: Ct));
    }

    [Fact]
    public async Task An_empty_batch_returns_no_counts_and_a_batch_runs_only_once()
    {
        await using var session = Session();
        var empty = session.CreateBatch("batch.empty");
        Assert.Empty(await empty.ExecuteAsync(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => empty.ExecuteAsync(Ct));
        Assert.Throws<InvalidOperationException>(() => empty.Add("SELECT 1"));
    }
}
