using System;
using System.Diagnostics;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using MySqlConnector;
using Testcontainers.MariaDb;
using Testcontainers.MySql;
using VelQuery.MySql.IntegrationTests;
using Xunit;

[assembly: AssemblyFixture(typeof(MySqlFixture))]

namespace VelQuery.MySql.IntegrationTests;

/// <summary>
/// One MySQL (or MariaDB) instance per test run (xUnit v3 assembly fixture). Uses Testcontainers with the official
/// <c>mysql:8.4</c> image (multi-arch, so the real engine runs natively on ARM64 and x64) started with
/// <c>--local-infile=1</c> for bulk loads. <c>VELQUERY_TEST_MYSQL_IMAGE</c> selects another image (for example
/// <c>mariadb:11</c>); <c>VELQUERY_TEST_MYSQL</c> supplies a connection string to an existing server instead (it must allow
/// <c>local_infile</c> and grant the user SUPER/CONNECTION_ADMIN for the KILL and SET GLOBAL tests).
/// </summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    public const string ConnectionStringVariable = "VELQUERY_TEST_MYSQL";
    public const string ImageVariable = "VELQUERY_TEST_MYSQL_IMAGE";
    public const string DefaultImage = "mysql:8.4";
    public const int LargeRowCount = 100_000;

    private IDatabaseContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public string Source { get; private set; } = string.Empty;

    public bool IsMariaDb { get; private set; }

    public string ServerVersion { get; private set; } = string.Empty;

    public TimeSpan StartupTime { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        var external = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        string serverConnectionString;
        if (!string.IsNullOrWhiteSpace(external))
        {
            serverConnectionString = external;
            Source = "external";
        }
        else
        {
            var image = Environment.GetEnvironmentVariable(ImageVariable);
            Source = string.IsNullOrWhiteSpace(image) ? DefaultImage : image;
            if (Source.Contains("mariadb", StringComparison.OrdinalIgnoreCase))
            {
                var mariaDb = new MariaDbBuilder(Source).WithUsername("root").WithPassword("velquery_it").WithDatabase("velquery_it")
                    .WithCommand("--local-infile=1", "--performance-schema=OFF").Build();
                await mariaDb.StartAsync(TestContext.Current.CancellationToken);
                _container = mariaDb;
                serverConnectionString = mariaDb.GetConnectionString();
            }
            else
            {
                var mySql = new MySqlBuilder(Source).WithUsername("root").WithPassword("velquery_it").WithDatabase("velquery_it")
                    .WithCommand("--local-infile=1", "--performance-schema=OFF").Build();
                await mySql.StartAsync(TestContext.Current.CancellationToken);
                _container = mySql;
                serverConnectionString = mySql.GetConnectionString();
            }
        }

        ConnectionString = new MySqlConnectionStringBuilder(serverConnectionString)
        {
            Pooling = true,
            AllowLoadLocalInfile = true,
            ApplicationName = "VelQuery.IntegrationTests",
        }.ConnectionString;

        await using (var connection = new MySqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            ServerVersion = connection.ServerVersion;
            IsMariaDb = ServerVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
            await using var command = new MySqlCommand(Schema, connection);
            await command.ExecuteNonQueryAsync();
        }

        StartupTime = stopwatch.Elapsed;
        TestContext.Current.SendDiagnosticMessage($"MySQL fixture: {Source}, server {ServerVersion}, ready in {StartupTime.TotalSeconds:F1}s");
    }

    public async ValueTask DisposeAsync()
    {
        MySqlConnection.ClearAllPools();
        if (_container is not null)
        {
            await _container.StopAsync();
            await _container.DisposeAsync();
        }
    }

    public string ConnectionStringWith(Action<MySqlConnectionStringBuilder> configure)
    {
        var builder = new MySqlConnectionStringBuilder(ConnectionString);
        configure(builder);
        return builder.ConnectionString;
    }

    public VelQueryDataSource CreateDataSource(VelQueryOptions? options = null)
        => VelQueryMySql.CreateDataSource(ConnectionString, options ?? new VelQueryOptions { AllowDynamicFallback = true });

    public VelQueryDataSource CreateDataSource(Action<MySqlConnectionStringBuilder> configure, VelQueryOptions? options = null)
        => VelQueryMySql.CreateDataSource(ConnectionStringWith(configure), options ?? new VelQueryOptions { AllowDynamicFallback = true });

    // Idempotent so an external server can be reused across runs. Generated with a digits cross join because recursive
    // CTE limits differ between MySQL (cte_max_recursion_depth) and MariaDB.
    private const string Schema = """
        DROP TABLE IF EXISTS rows100, rows100k, types, victims, digits;
        DROP PROCEDURE IF EXISTS double_it;
        DROP PROCEDURE IF EXISTS insert_victim;
        DROP PROCEDURE IF EXISTS count_upto;

        CREATE TABLE digits (d int NOT NULL);
        INSERT INTO digits VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9);

        CREATE TABLE rows100 (id bigint PRIMARY KEY, name varchar(50) NOT NULL, category int NULL);
        INSERT INTO rows100 SELECT n, CONCAT('row-', n), CASE WHEN n % 10 = 0 THEN NULL ELSE n % 3 END
            FROM (SELECT 1 + a.d + 10 * b.d AS n FROM digits a CROSS JOIN digits b) s;

        CREATE TABLE rows100k (id bigint PRIMARY KEY, name varchar(50) NOT NULL, payload varchar(200) NOT NULL);
        INSERT INTO rows100k SELECT n, CONCAT('name-', n), REPEAT('x', 150)
            FROM (SELECT 1 + a.d + 10 * b.d + 100 * c.d + 1000 * e.d + 10000 * f.d AS n
                  FROM digits a CROSS JOIN digits b CROSS JOIN digits c CROSS JOIN digits e CROSS JOIN digits f) s;

        CREATE TABLE types (
            id int PRIMARY KEY,
            i1 tinyint, i2 smallint, i4 int, i8 bigint, u8 bigint unsigned, num decimal(28, 8), big decimal(38, 4),
            flag tinyint(1), bits bit(1), txt text, uid char(36), ubin binary(16), dt datetime(6), ts timestamp(6) NULL,
            d date, tm time(6), tm_long time, js json, blb longblob, f float, dbl double
        ) CHARACTER SET utf8mb4;
        INSERT INTO types VALUES
            (1, -128, 32767, 2147483647, 9223372036854775807, 18446744073709551615, 12345678901234567890.12345678,
             1234567890123456789012345678901234.5678, 1, b'1', CONCAT('💡 unicode ✓ ', REPEAT('é', 5000)),
             'a3c1d865-9684-06a4-f000-000000000001', x'a3c1d865968406a4f000000000000001',
             '2026-10-11 08:30:15.123456', '2026-10-11 08:30:15.123456', '2026-10-11', '13:45:07.5', '-800:00:00',
             '{"a": [1, 2]}', x'00ff10', 1.5, 2.25),
            (2, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);

        CREATE TABLE victims (id int PRIMARY KEY, name varchar(50) NOT NULL);
        INSERT INTO victims VALUES (1, 'alice'), (2, 'bob');

        CREATE PROCEDURE double_it(INOUT v int) SET v = v * 2;
        CREATE PROCEDURE insert_victim(IN p_id int, IN p_name varchar(50)) INSERT INTO victims VALUES (p_id, p_name);
        CREATE PROCEDURE count_upto(IN max_id bigint, OUT total bigint) SELECT count(*) INTO total FROM rows100 WHERE id <= max_id;
        """;
}

/// <summary>Base for tests sharing the assembly-wide MySQL fixture.</summary>
public abstract class IntegrationTest(MySqlFixture fixture)
{
    protected MySqlFixture Fixture { get; } = fixture;

    protected static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

    protected VelQuerySession Session(VelQueryOptions? options = null) => Fixture.CreateDataSource(options).CreateSession();

    /// <summary>A data source with its own small pool so leaks surface as pool timeouts.</summary>
    protected VelQueryDataSource IsolatedPoolDataSource(int maxPoolSize, string poolTag)
        => VelQueryMySql.CreateDataSource(Fixture.ConnectionStringWith(b =>
        {
            b.MaximumPoolSize = (uint)maxPoolSize;
            b.MinimumPoolSize = 0;
            b.ConnectionTimeout = 5;
            b.ApplicationName = "VelQuery.It." + poolTag + "." + Guid.NewGuid().ToString("N")[..8];
        }));

    protected async Task<string> CreateTableAsync(string columns)
    {
        var table = "t_" + Guid.NewGuid().ToString("N");
        await using var session = Session();
        await session.ExecuteAsync($"CREATE TABLE {table} ({columns}) ENGINE=InnoDB CHARACTER SET utf8mb4", cancellationToken: Ct);
        return table;
    }
}

public sealed record IdName(long Id, string Name);
