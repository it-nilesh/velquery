using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VelQuery.MySql.IntegrationTests;

// The same generated contracts that run on SQL Server and PostgreSQL, here through MySqlConnector with SequentialAccess.

public sealed record MyRow(long Id, string Name, int? Category);

public sealed record MyStrictRow(long Id, string Name, int Category);

[VelQuerySql("mysql.rows100", "SELECT id, name, category FROM rows100 ORDER BY id")]
public static partial class MyRows100
{
    public static partial Task<IReadOnlyList<MyRow>> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("mysql.rows100-shuffled", "SELECT category, name, id FROM rows100 ORDER BY id")]
public static partial class MyRows100Shuffled
{
    public static partial IAsyncEnumerable<MyRow> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("mysql.rows100-strict", "SELECT id, name, category FROM rows100 ORDER BY id")]
public static partial class MyRows100Strict
{
    public static partial Task<IReadOnlyList<MyStrictRow>> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("mysql.by-id", "SELECT id, name, category FROM rows100 WHERE id = @Id")]
public static partial class MyRowById
{
    public static partial Task<MyRow?> ExecuteAsync(IVelQuerySession session, long id, CancellationToken cancellationToken = default);
}

// Backtick-quoted identifiers are plain text to the (T-SQL-lexical) scanner; only @Id is a placeholder.
[VelQuerySql("mysql.by-id-backticks", "SELECT `id`, `name`, `category` FROM `rows100` WHERE `id` = @Id")]
public static partial class MyRowByIdBackticks
{
    public static partial Task<MyRow?> ExecuteAsync(IVelQuerySession session, long id, CancellationToken cancellationToken = default);
}

[VelQuerySql("mysql.rename", "UPDATE rows100 SET name = @Name WHERE id = @Id", Cardinality = QueryCardinality.NonQuery)]
public static partial class MyRename
{
    public static partial Task<int> ExecuteAsync(IVelQuerySession session, long id, string name, CancellationToken cancellationToken = default);
}

public sealed class GeneratedContractTests(MySqlFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Generated_contracts_run_unchanged_on_mysql()
    {
        await using var session = Session(new VelQueryOptions());

        var rows = await MyRows100.ExecuteAsync(session, Ct);

        Assert.Equal(100, rows.Count);
        Assert.Equal(new MyRow(1, "row-1", 1), rows[0]);
        Assert.Equal(new MyRow(10, "row-10", null), rows[9]);
        Assert.Equal(new MyRow(42, "row-42", 0), await MyRowById.ExecuteAsync(session, 42, Ct));
        Assert.Equal(new MyRow(42, "row-42", 0), await MyRowByIdBackticks.ExecuteAsync(session, 42, Ct));
        Assert.Null(await MyRowById.ExecuteAsync(session, -1, Ct));
    }

    [Fact]
    public async Task Shuffled_select_order_maps_identically()
    {
        await using var session = Session(new VelQueryOptions());
        var expected = await MyRows100.ExecuteAsync(session, Ct);

        var shuffled = new List<MyRow>();
        await foreach (var row in MyRows100Shuffled.ExecuteAsync(session, Ct))
        {
            shuffled.Add(row);
        }

        Assert.Equal(expected, shuffled);
    }

    [Fact]
    public async Task Strict_null_error_names_the_column_from_mysqlconnector_getters()
    {
        await using var session = Session(new VelQueryOptions());

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => MyRows100Strict.ExecuteAsync(session, Ct));

        Assert.Equal("category", ex.Column, ignoreCase: true);
        Assert.Equal("mysql.rows100-strict", ex.QueryId);
    }

    [Fact]
    public async Task Generated_non_query_binds_parameters_inside_a_transaction()
    {
        await using var session = Session(new VelQueryOptions());
        await using (await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            Assert.Equal(1, await MyRename.ExecuteAsync(session, 5, "renamed", Ct));
            Assert.Equal("renamed", (await MyRowById.ExecuteAsync(session, 5, Ct))!.Name);
        }

        // Disposed without commit: rolled back.
        Assert.Equal("row-5", (await MyRowById.ExecuteAsync(session, 5, Ct))!.Name);
    }
}
