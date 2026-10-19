using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VelQuery.Providers;
using VelQuery.Sqlite.Bulk;
using Xunit;

namespace VelQuery.Sqlite.IntegrationTests;

/// <summary>
/// The generic ADO.NET tier (PRD amendment VQ-DB-003) driven by a real driver: <see cref="VelQueryAdoNet"/> over
/// <see cref="SqliteFactory"/>. Core semantics hold; the reduced guarantees (no value conversions unless declared, no
/// timeout or cancellation classification) are pinned down so the documentation matches behavior.
/// </summary>
public sealed class GenericProviderTests(SqliteFixture fixture) : IntegrationTest(fixture)
{
    private VelQueryDataSource Generic(GenericAdoNetProviderOptions? dialect = null, VelQueryOptions? options = null, string? connectionString = null)
        => VelQueryAdoNet.CreateDataSource(SqliteFactory.Instance, connectionString ?? Fixture.ConnectionString, dialect, options ?? new VelQueryOptions { AllowDynamicFallback = true });

    [Fact]
    public async Task Core_operations_work_through_the_generic_provider()
    {
        await using var dataSource = Generic();
        await using var session = dataSource.CreateSession();

        Assert.Equal("generic", session.Provider.DialectId);
        Assert.Equal(100, (await session.QueryAsync<IdName>("SELECT id, name FROM rows100 ORDER BY id", null, Ct)).Count);
        Assert.Equal(new IdName(7, "row-7"), await session.QuerySingleAsync<IdName>("SELECT id, name FROM rows100 WHERE id = @Id", new QueryParameters().Add("Id", 7L), Ct));
        await Assert.ThrowsAsync<VelQueryNoRowsException>(() => session.QueryFirstAsync<IdName>("SELECT id, name FROM rows100 WHERE 0", null, Ct));
        await Assert.ThrowsAsync<VelQueryTooManyRowsException>(() => session.QuerySingleAsync<IdName>("SELECT id, name FROM rows100", null, Ct));
        // Int64 → int narrowing is VelQuery's range-checked ValueConverter rule here, not the driver's.
        Assert.Equal(100, await session.ExecuteScalarAsync<int>("SELECT count(*) FROM rows100", cancellationToken: Ct));
        Assert.Null(await session.ExecuteScalarAsync<int?>("SELECT category FROM rows100 WHERE id = 10", cancellationToken: Ct));
        Assert.Empty(await session.QueryAsync<IdName>("SELECT id, name FROM victims WHERE name = @Name", new QueryParameters().Add("Name", "' OR 1=1 --"), Ct));

        await using (var multi = await session.QueryMultipleAsync("SELECT 1; SELECT 'two'", new QueryParameters(), Ct))
        {
            Assert.Equal(1L, await multi.ReadSingleAsync<long>(Ct));
            Assert.Equal("two", await multi.ReadSingleAsync<string>(Ct));
        }

        long streamed = 0;
        await foreach (var id in session.StreamAsync<long>("SELECT id FROM rows100k", null, Ct))
        {
            streamed += id;
        }

        Assert.Equal(SqliteFixture.LargeRowCount * (SqliteFixture.LargeRowCount + 1L) / 2, streamed);
    }

    [Fact]
    public async Task Generated_contracts_and_transactions_work_through_the_generic_provider()
    {
        await using var dataSource = Generic(options: new VelQueryOptions());
        await using var session = dataSource.CreateSession();

        Assert.Equal(await LiteRows100.ExecuteAsync(session, Ct), await LiteRows100.ExecuteAsync(Session(new VelQueryOptions()), Ct));
        var strict = await Assert.ThrowsAsync<VelQueryMappingException>(() => LiteRows100Strict.ExecuteAsync(session, Ct));
        Assert.Equal("category", strict.Column, ignoreCase: true);

        await using (await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            Assert.Equal(1, await LiteRename.ExecuteAsync(session, 6, "generic", Ct));
            Assert.Equal("generic", (await LiteRowById.ExecuteAsync(session, 6, Ct))!.Name);
        }

        Assert.Equal("row-6", (await LiteRowById.ExecuteAsync(session, 6, Ct))!.Name);
    }

    [Fact]
    public async Task Without_native_conversions_stored_representations_are_rejected_by_the_strict_converter()
    {
        await using var dataSource = Generic(options: new VelQueryOptions());
        await using var session = dataSource.CreateSession();

        var row = await Assert.ThrowsAsync<VelQueryMappingException>(() => LiteTypes.ExecuteAsync(session, Ct));
        Assert.Equal("num", row.Column, ignoreCase: true); // TEXT → decimal is the first member that needs parsing

        // VelQuery never parses strings or treats integers as booleans; the dedicated provider delegates that to the driver.
        await Assert.ThrowsAsync<VelQueryMappingException>(() => session.ExecuteScalarAsync<DateTime>("SELECT ts FROM types WHERE id = 1", cancellationToken: Ct));
        await Assert.ThrowsAsync<VelQueryMappingException>(() => session.ExecuteScalarAsync<Guid>("SELECT uid FROM types WHERE id = 1", cancellationToken: Ct));
        await Assert.ThrowsAsync<VelQueryMappingException>(() => session.ExecuteScalarAsync<bool>("SELECT flag FROM types WHERE id = 1", cancellationToken: Ct));
        // Lossless rules still apply: integers to enums, and SQLite REAL (double) stays double.
        Assert.Equal(LiteColor.Green, await session.ExecuteScalarAsync<LiteColor>("SELECT color FROM types WHERE id = 1", cancellationToken: Ct));
        Assert.Equal(LiteColor.Green, await session.ExecuteScalarAsync<LiteColor?>("SELECT color FROM types WHERE id = 1", cancellationToken: Ct));
        // ...and SQLite's lossy coercions do not happen.
        await Assert.ThrowsAsync<VelQueryMappingException>(() => session.ExecuteScalarAsync<int>("SELECT 1.9", cancellationToken: Ct));
    }

    [Fact]
    public async Task Declaring_native_conversions_on_the_generic_provider_matches_the_dedicated_provider()
    {
        var dialect = new GenericAdoNetProviderOptions { DialectId = "sqlite-generic", Capabilities = ProviderCapabilities.MultipleResults | ProviderCapabilities.NativeConversions };
        await using var dataSource = Generic(dialect, new VelQueryOptions());
        await using var session = dataSource.CreateSession();
        await using var dedicated = Session(new VelQueryOptions());

        var generic = await LiteTypes.ExecuteAsync(session, Ct);
        var expected = await LiteTypes.ExecuteAsync(dedicated, Ct);

        Assert.Equal(expected.Select(r => r with { Bytes = null }), generic.Select(r => r with { Bytes = null }));
        Assert.Equal(expected[0].Bytes, generic[0].Bytes);
    }

    [Fact]
    public async Task Lock_timeouts_are_not_classified_by_the_generic_provider()
    {
        var connectionString = Fixture.NewDatabase("generic-locked");
        await using var holder = new SqliteConnection(connectionString);
        await holder.OpenAsync(Ct);
        await using (var command = holder.CreateCommand())
        {
            command.CommandText = "CREATE TABLE x (id INTEGER PRIMARY KEY); BEGIN IMMEDIATE; INSERT INTO x VALUES (1);";
            await command.ExecuteNonQueryAsync(Ct);
        }

        await using var generic = Generic(connectionString: connectionString);
        await using var genericSession = generic.CreateSession();
        await using var dedicated = VelQuerySqlite.CreateDataSource(connectionString);
        await using var dedicatedSession = dedicated.CreateSession();

        // Same driver failure (SQLITE_BUSY after the 1 s lock wait): a provider error here, a timeout with the dedicated provider.
        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() =>
            genericSession.Sql("INSERT INTO x VALUES (2)").WithTimeout(TimeSpan.FromSeconds(1)).ExecuteAsync(Ct));
        Assert.Equal(5, Assert.IsType<SqliteException>(ex.InnerException).SqliteErrorCode);
        await Assert.ThrowsAsync<VelQueryTimeoutException>(() =>
            dedicatedSession.Sql("INSERT INTO x VALUES (2)").WithTimeout(TimeSpan.FromSeconds(1)).ExecuteAsync(Ct));
    }

    [Fact]
    public async Task Provider_interrupts_are_plain_provider_errors_on_the_generic_provider()
    {
        await using var connection = new SqliteConnection(Fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var dataSource = Generic();
        await using var session = dataSource.CreateSession(connection);
        using var interrupt = new Timer(_ => SQLitePCL.raw.sqlite3_interrupt(connection.Handle), null, 200, Timeout.Infinite);

        var ex = await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteScalarAsync<long>(SlowAggregate(200), cancellationToken: Ct));

        Assert.Equal(9, Assert.IsType<SqliteException>(ex.InnerException).SqliteErrorCode);
    }

    [Fact]
    public async Task Connections_are_released_through_the_generic_provider()
    {
        var counting = new CountingDataSource(Fixture.ConnectionString);
        await using var dataSource = VelQueryAdoNet.CreateDataSource(counting, options: new VelQueryOptions { AllowDynamicFallback = true }, ownsDataSource: true);
        for (var i = 0; i < 20; i++)
        {
            await using var session = dataSource.CreateSession();
            if (i % 2 == 0)
            {
                await Assert.ThrowsAsync<VelQueryProviderException>(() => session.ExecuteAsync("SELECT * FROM missing", cancellationToken: Ct));
            }
            else
            {
                await foreach (var _ in session.StreamAsync<long>("SELECT id FROM rows100k", null, Ct))
                {
                    break;
                }
            }
        }

        Assert.Equal(20, counting.Created);
        Assert.Equal(0, counting.Outstanding);
    }

    [Fact]
    public async Task The_sqlite_bulk_path_needs_only_a_sqlite_connection_but_errors_keep_generic_classification()
    {
        var table = await CreateTableAsync("id INTEGER PRIMARY KEY, name TEXT NOT NULL");
        var plan = SqliteBulkInsertPlan<IdName>.Create(table).Column("id", r => r.Id).Column("name", r => r.Name);
        await using var dataSource = Generic();
        await using var session = dataSource.CreateSession();

        Assert.Equal(1_000, await session.BulkInsertAsync(plan, Rows(1_000), Ct));
        await Assert.ThrowsAsync<VelQueryProviderException>(() => session.BulkInsertAsync(plan, Rows(1), Ct));
        Assert.Equal(1_000L, await session.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", cancellationToken: Ct));

        static IEnumerable<IdName> Rows(int count) => Enumerable.Range(1, count).Select(i => new IdName(i, "n" + i));
    }
}
