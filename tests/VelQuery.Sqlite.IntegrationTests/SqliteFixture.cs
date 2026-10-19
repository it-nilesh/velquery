using System;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VelQuery.Sqlite.IntegrationTests;
using Xunit;

[assembly: AssemblyFixture(typeof(SqliteFixture))]

namespace VelQuery.Sqlite.IntegrationTests;

/// <summary>
/// One temp-file SQLite database per test run (xUnit v3 assembly fixture), with the shared read-only tables. Tests that
/// need lock contention or isolation create their own files under <see cref="Directory"/>. No Docker required: the
/// engine is the SQLite library bundled with Microsoft.Data.Sqlite (SQLitePCLRaw e_sqlite3).
/// </summary>
public sealed class SqliteFixture : IAsyncLifetime
{
    public const int LargeRowCount = 100_000;

    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "velquery-sqlite-it-" + Guid.NewGuid().ToString("N")[..8]);

    public string DatabasePath => Path.Combine(Directory, "main.db");

    public string ConnectionString { get; private set; } = string.Empty;

    public string EngineVersion { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        System.IO.Directory.CreateDirectory(Directory);
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = true }.ConnectionString;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        EngineVersion = connection.ServerVersion;
        await using var command = connection.CreateCommand();
        command.CommandText = Schema;
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>A fresh empty database file in the run directory.</summary>
    public string NewDatabase(string tag, Action<SqliteConnectionStringBuilder>? configure = null)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Directory, tag + "-" + Guid.NewGuid().ToString("N")[..8] + ".db"), Pooling = true };
        configure?.Invoke(builder);
        return builder.ConnectionString;
    }

    public VelQueryDataSource CreateDataSource(VelQueryOptions? options = null)
        => VelQuerySqlite.CreateDataSource(ConnectionString, options ?? new VelQueryOptions { AllowDynamicFallback = true });

    // Text formats in the types row are the ones Microsoft.Data.Sqlite writes for parameters, so literal and bound
    // values compare equal. Decimals live in a TEXT column: NUMERIC affinity would turn them into lossy REALs.
    private const string Schema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE rows100 (id INTEGER PRIMARY KEY, name TEXT NOT NULL, category INTEGER NULL);
        WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 100)
        INSERT INTO rows100 SELECT x, 'row-' || x, CASE WHEN x % 10 = 0 THEN NULL ELSE x % 3 END FROM n;

        CREATE TABLE rows100k (id INTEGER PRIMARY KEY, name TEXT NOT NULL, payload TEXT NOT NULL);
        WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 100000)
        INSERT INTO rows100k SELECT x, 'name-' || x, replace(hex(zeroblob(150)), '00', 'x') FROM n;

        CREATE TABLE victims (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
        INSERT INTO victims VALUES (1, 'alice'), (2, 'bob');

        CREATE TABLE types (
            id INTEGER PRIMARY KEY, i2 INTEGER, i4 INTEGER, i8 INTEGER, num TEXT, flag INTEGER, txt TEXT, uid TEXT,
            ts TEXT, tstz TEXT, d TEXT, tm TEXT, span TEXT, bytes BLOB, dbl REAL, flt REAL, color INTEGER, u8 INTEGER, ch TEXT);
        INSERT INTO types VALUES
            (1, 32767, 2147483647, 9223372036854775807, '12345678901234567890.12345678', 1,
             '💡 unicode ✓ ' || replace(hex(zeroblob(5000)), '00', 'é'), 'A3C1D865-9684-06A4-F000-000000000001',
             '2026-10-11 08:30:15.123456', '2026-10-11 08:30:15.123456+05:30', '2026-10-11', '13:45:07.5000000',
             '1.06:30:00', x'00ff10', 1.5, 0.25, 2, 200, 'x'),
            (2, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
        """;
}

/// <summary>Base for tests sharing the assembly-wide SQLite fixture.</summary>
public abstract class IntegrationTest(SqliteFixture fixture)
{
    protected SqliteFixture Fixture { get; } = fixture;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected VelQuerySession Session(VelQueryOptions? options = null) => Fixture.CreateDataSource(options).CreateSession();

    protected async Task<string> CreateTableAsync(string columns)
    {
        var table = "t_" + Guid.NewGuid().ToString("N");
        await using var session = Session();
        await session.ExecuteAsync($"CREATE TABLE {table} ({columns})", cancellationToken: Ct);
        return table;
    }

    /// <summary>
    /// A query that runs for roughly <paramref name="millions"/> × 60 ms of CPU in a single step (an aggregate), with no
    /// rows returned in between.
    /// </summary>
    protected static string SlowAggregate(int millions)
        => $"WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < {millions}000000) SELECT count(*) FROM c";
}

/// <summary>
/// A <see cref="DbDataSource"/> that counts the connections it hands out and how many were disposed, so tests can prove
/// VelQuery releases every connection (Microsoft.Data.Sqlite's pool is unbounded, so leaks never surface as pool
/// timeouts as they do with SqlClient or Npgsql).
/// </summary>
public sealed class CountingDataSource(string connectionString) : DbDataSource
{
    private int _created;
    private int _disposed;

    public int Created => Volatile.Read(ref _created);

    public int Outstanding => Volatile.Read(ref _created) - Volatile.Read(ref _disposed);

    public override string ConnectionString => connectionString;

    protected override DbConnection CreateDbConnection()
    {
        var connection = new SqliteConnection(connectionString);
        Interlocked.Increment(ref _created);
        connection.Disposed += (_, _) => Interlocked.Increment(ref _disposed);
        return connection;
    }
}

public sealed record IdName(long Id, string Name);
