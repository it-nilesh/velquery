using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace VelQuery.PostgreSQL.IntegrationTests;

// The same generated contracts that run on SQL Server, here through Npgsql with SequentialAccess.

public sealed record PgRow(long Id, string Name, int? Category);

public sealed record PgStrictRow(long Id, string Name, int Category);

[VelQuerySql("pg.rows100", "SELECT id, name, category FROM rows100 ORDER BY id")]
public static partial class PgRows100
{
    public static partial Task<IReadOnlyList<PgRow>> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("pg.rows100-shuffled", "SELECT category, name, id FROM rows100 ORDER BY id")]
public static partial class PgRows100Shuffled
{
    public static partial IAsyncEnumerable<PgRow> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("pg.rows100-strict", "SELECT id, name, category FROM rows100 ORDER BY id")]
public static partial class PgRows100Strict
{
    public static partial Task<IReadOnlyList<PgStrictRow>> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("pg.by-id", "SELECT id, name, category FROM rows100 WHERE id = @Id")]
public static partial class PgRowById
{
    public static partial Task<PgRow?> ExecuteAsync(IVelQuerySession session, long id, CancellationToken cancellationToken = default);
}

[VelQuerySql("pg.rename", "UPDATE rows100 SET name = @Name WHERE id = @Id", Cardinality = QueryCardinality.NonQuery)]
public static partial class PgRename
{
    public static partial Task<int> ExecuteAsync(IVelQuerySession session, long id, string name, CancellationToken cancellationToken = default);
}

public sealed class GeneratedContractTests(PostgreSqlFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Generated_contracts_run_unchanged_on_postgresql()
    {
        await using var session = Session(new VelQueryOptions());

        var rows = await PgRows100.ExecuteAsync(session, Ct);

        Assert.Equal(100, rows.Count);
        Assert.Equal(new PgRow(1, "row-1", 1), rows[0]);
        Assert.Equal(new PgRow(10, "row-10", null), rows[9]);
        Assert.Equal(new PgRow(42, "row-42", 0), await PgRowById.ExecuteAsync(session, 42, Ct));
        Assert.Null(await PgRowById.ExecuteAsync(session, -1, Ct));
    }

    [Fact]
    public async Task Shuffled_select_order_maps_identically()
    {
        await using var session = Session(new VelQueryOptions());
        var expected = await PgRows100.ExecuteAsync(session, Ct);

        var shuffled = new List<PgRow>();
        await foreach (var row in PgRows100Shuffled.ExecuteAsync(session, Ct))
        {
            shuffled.Add(row);
        }

        Assert.Equal(expected, shuffled);
    }

    [Fact]
    public async Task Strict_null_error_names_the_column_from_npgsql_getters()
    {
        await using var session = Session(new VelQueryOptions());

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => PgRows100Strict.ExecuteAsync(session, Ct));

        Assert.Equal("category", ex.Column, ignoreCase: true);
        Assert.Equal("pg.rows100-strict", ex.QueryId);
    }

    [Fact]
    public async Task Generated_non_query_binds_parameters_inside_a_transaction()
    {
        await using var session = Session(new VelQueryOptions());
        await using (await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            Assert.Equal(1, await PgRename.ExecuteAsync(session, 5, "renamed", Ct));
            Assert.Equal("renamed", (await PgRowById.ExecuteAsync(session, 5, Ct))!.Name);
        }

        // Disposed without commit: rolled back.
        Assert.Equal("row-5", (await PgRowById.ExecuteAsync(session, 5, Ct))!.Name);
    }
}
