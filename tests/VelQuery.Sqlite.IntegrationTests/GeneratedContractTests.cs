using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VelQuery.Sqlite.IntegrationTests;

// The same contract shapes that run on SQL Server and PostgreSQL, here through Microsoft.Data.Sqlite. SQLite returns
// every integer as Int64, so int members always take the converting (NativeConversions) path.

public sealed record LiteRow(long Id, string Name, int? Category);

public sealed record LiteStrictRow(long Id, string Name, int Category);

[VelQuerySql("sqlite.rows100", "SELECT id, name, category FROM rows100 ORDER BY id")]
public static partial class LiteRows100
{
    public static partial Task<IReadOnlyList<LiteRow>> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("sqlite.rows100-shuffled", "SELECT category, name, id FROM rows100 ORDER BY id")]
public static partial class LiteRows100Shuffled
{
    public static partial IAsyncEnumerable<LiteRow> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("sqlite.rows100-strict", "SELECT id, name, category FROM rows100 ORDER BY id")]
public static partial class LiteRows100Strict
{
    public static partial Task<IReadOnlyList<LiteStrictRow>> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("sqlite.by-id", "SELECT id, name, category FROM rows100 WHERE id = @Id")]
public static partial class LiteRowById
{
    public static partial Task<LiteRow?> ExecuteAsync(IVelQuerySession session, long id, CancellationToken cancellationToken = default);
}

[VelQuerySql("sqlite.rename", "UPDATE rows100 SET name = @Name WHERE id = @Id", Cardinality = QueryCardinality.NonQuery)]
public static partial class LiteRename
{
    public static partial Task<int> ExecuteAsync(IVelQuerySession session, long id, string name, CancellationToken cancellationToken = default);
}

[VelQuerySql("sqlite.count", "SELECT count(*) FROM rows100 WHERE category = @Category", Cardinality = QueryCardinality.Scalar)]
public static partial class LiteCountByCategory
{
    public static partial Task<int> ExecuteAsync(IVelQuerySession session, int category, CancellationToken cancellationToken = default);
}

// SQLite-specific syntax the T-SQL-lexical scanner handles: "quoted" and [bracketed] identifiers, -- and /* */ comments,
// string literals containing @, and the || operator.
[VelQuerySql("sqlite.syntax", """
    SELECT "id", [name] || '@not_a_parameter' AS name, category -- @AlsoNotAParameter
    FROM rows100 /* @NorThis */ WHERE "id" = @Id
    """)]
public static partial class LiteSyntax
{
    public static partial Task<LiteRow> ExecuteAsync(IVelQuerySession session, long id, CancellationToken cancellationToken = default);
}

public sealed class GeneratedContractTests(SqliteFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Generated_contracts_run_unchanged_on_sqlite()
    {
        await using var session = Session(new VelQueryOptions());

        var rows = await LiteRows100.ExecuteAsync(session, Ct);

        Assert.Equal(100, rows.Count);
        Assert.Equal(new LiteRow(1, "row-1", 1), rows[0]);
        Assert.Equal(new LiteRow(10, "row-10", null), rows[9]);
        Assert.Equal(new LiteRow(42, "row-42", 0), await LiteRowById.ExecuteAsync(session, 42, Ct));
        Assert.Null(await LiteRowById.ExecuteAsync(session, -1, Ct));
        Assert.Equal(30, await LiteCountByCategory.ExecuteAsync(session, 1, Ct));
    }

    [Fact]
    public async Task Shuffled_select_order_maps_identically()
    {
        await using var session = Session(new VelQueryOptions());
        var expected = await LiteRows100.ExecuteAsync(session, Ct);

        var shuffled = new List<LiteRow>();
        await foreach (var row in LiteRows100Shuffled.ExecuteAsync(session, Ct))
        {
            shuffled.Add(row);
        }

        Assert.Equal(expected, shuffled);
    }

    [Fact]
    public async Task Strict_null_error_names_the_column_from_sqlite_getters()
    {
        await using var session = Session(new VelQueryOptions());

        var ex = await Assert.ThrowsAsync<VelQueryMappingException>(() => LiteRows100Strict.ExecuteAsync(session, Ct));

        Assert.Equal("category", ex.Column, ignoreCase: true);
        Assert.Equal("sqlite.rows100-strict", ex.QueryId);
    }

    [Fact]
    public async Task Lenient_mapping_reads_null_as_default()
    {
        await using var session = Session(new VelQueryOptions { StrictMapping = false });

        var rows = await LiteRows100Strict.ExecuteAsync(session, Ct);

        Assert.Equal(0, rows[9].Category);
    }

    [Fact]
    public async Task Generated_non_query_binds_parameters_inside_a_transaction()
    {
        await using var session = Session(new VelQueryOptions());
        await using (await session.BeginTransactionAsync(cancellationToken: Ct))
        {
            Assert.Equal(1, await LiteRename.ExecuteAsync(session, 5, "renamed", Ct));
            Assert.Equal("renamed", (await LiteRowById.ExecuteAsync(session, 5, Ct))!.Name);
        }

        // Disposed without commit: rolled back.
        Assert.Equal("row-5", (await LiteRowById.ExecuteAsync(session, 5, Ct))!.Name);
    }

    [Fact]
    public async Task Quoted_identifiers_comments_and_literals_are_not_placeholders()
    {
        await using var session = Session(new VelQueryOptions());

        Assert.Equal(new LiteRow(3, "row-3@not_a_parameter", 0), await LiteSyntax.ExecuteAsync(session, 3, Ct));
    }
}
