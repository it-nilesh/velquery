using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.MySql;
using VelQuery.PostgreSQL;
using VelQuery.PostgreSQL.Bulk;
using VelQuery.Sqlite;
using VelQuery.Sqlite.Bulk;

namespace VelQuery.NativeAot.Smoke;

public sealed record SmokeItem(int Id, string Name, decimal? Price);

// Generated contracts: binders and mappers are emitted at build time, so these paths are reflection-free under AOT.
[VelQuerySql("smoke.by-id", "SELECT id, name, price FROM smoke_items WHERE id = @Id")]
public static partial class ItemById
{
    public static partial Task<SmokeItem?> ExecuteAsync(IVelQuerySession session, int id, CancellationToken cancellationToken = default);
}

[VelQuerySql("smoke.all", "SELECT id, name, price FROM smoke_items ORDER BY id")]
public static partial class AllItems
{
    public static partial IAsyncEnumerable<SmokeItem> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}

[VelQuerySql("smoke.insert", "INSERT INTO smoke_items (id, name, price) VALUES (@Id, @Name, @Price)", Cardinality = QueryCardinality.NonQuery)]
public static partial class InsertItem
{
    public static partial Task<int> ExecuteAsync(IVelQuerySession session, int id, string name, decimal? price, CancellationToken cancellationToken = default);
}

public static class Program
{
    public static async Task<int> Main()
    {
        var targets = new List<(string Name, Func<VelQueryDataSource> Create, Func<IVelQuerySession, IEnumerable<SmokeItem>, Task<long>>? Bulk)>();
        var sqliteFile = Path.Combine(Path.GetTempPath(), $"velquery-aot-{Guid.NewGuid():N}.db");
        targets.Add(("sqlite", () => VelQuerySqlite.CreateDataSource($"Data Source={sqliteFile}"),
            (s, rows) => s.BulkInsertAsync(SqliteBulkInsertPlan<SmokeItem>.Create("smoke_items")
                .Column("id", r => r.Id).Column("name", r => r.Name).Column("price", r => r.Price), rows)));
        if (Environment.GetEnvironmentVariable("VELQUERY_SMOKE_POSTGRES") is { Length: > 0 } pg)
        {
            targets.Add(("postgresql", () => VelQueryPostgreSql.CreateDataSource(pg),
                (s, rows) => s.BulkInsertAsync(PostgreSqlBulkInsertPlan<SmokeItem>.Create("smoke_items")
                    .Column("id", r => r.Id).Column("name", r => r.Name).Column("price", r => r.Price), rows)));
        }

        if (Environment.GetEnvironmentVariable("VELQUERY_SMOKE_MYSQL") is { Length: > 0 } my)
        {
            targets.Add(("mysql", () => VelQueryMySql.CreateDataSource(my), null));
        }

        var failures = 0;
        foreach (var (name, create, bulk) in targets)
        {
            try
            {
                await using var dataSource = create();
                await RunAsync(dataSource, bulk);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL {name}: {ex}");
            }
        }

        File.Delete(sqliteFile);
        return failures == 0 ? 0 : 1;
    }

    private static async Task RunAsync(VelQueryDataSource dataSource, Func<IVelQuerySession, IEnumerable<SmokeItem>, Task<long>>? bulk)
    {
        await using var session = dataSource.CreateSession();
        await session.ExecuteAsync("DROP TABLE IF EXISTS smoke_items");
        await session.ExecuteAsync("CREATE TABLE smoke_items (id INT PRIMARY KEY, name VARCHAR(100) NOT NULL, price DECIMAL(18, 2) NULL)");

        Check(await InsertItem.ExecuteAsync(session, 1, "keyboard", 129.50m) == 1, "generated insert affected 1 row");
        await InsertItem.ExecuteAsync(session, 2, "mouse", null);
        Check(await ItemById.ExecuteAsync(session, 1) == new SmokeItem(1, "keyboard", 129.50m), "generated single-row mapping");
        Check(await ItemById.ExecuteAsync(session, 99) is null, "generated SingleOrDefault returns null");

        // Runtime SQL resolves the generated mapper registered by the module initializer (no reflection fallback).
        var rows = await session.QueryAsync<SmokeItem>("SELECT id, name, price FROM smoke_items WHERE id >= @Min ORDER BY id",
            new QueryParameters().Add("Min", 1));
        Check(rows.Count == 2 && rows[1].Price is null, "runtime SQL with generated mapper and NULL");
        Check(await session.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM smoke_items") == 2, "scalar");

        var streamed = new List<int>();
        await foreach (var item in AllItems.ExecuteAsync(session))
        {
            streamed.Add(item.Id);
        }

        Check(streamed.SequenceEqual([1, 2]), "generated stream");

        await using (await session.BeginTransactionAsync())
        {
            await InsertItem.ExecuteAsync(session, 3, "rolled back", 1m);
        }

        Check(await ItemById.ExecuteAsync(session, 3) is null, "transaction rolled back on dispose");

        try
        {
            await session.QueryAsync<int>("SELECT price FROM smoke_items WHERE id = 2");
            throw new InvalidOperationException("expected a strict NULL mapping error");
        }
        catch (VelQueryMappingException)
        {
        }

        if (bulk is not null)
        {
            var written = await bulk(session, Enumerable.Range(100, 10_000).Select(i => new SmokeItem(i, "bulk-" + i, i % 3 == 0 ? null : i)));
            Check(written == 10_000, "bulk insert count");
            Check(await session.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM smoke_items") == 10_002, "bulk rows visible");
        }

        await session.ExecuteAsync("DROP TABLE smoke_items");
    }

    private static void Check(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("smoke check failed: " + what);
        }
    }
}
