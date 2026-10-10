using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Npgsql;
using Testcontainers.PostgreSql;
using VelQuery.PostgreSQL.IntegrationTests;
using Xunit;

[assembly: AssemblyFixture(typeof(PostgreSqlFixture))]

namespace VelQuery.PostgreSQL.IntegrationTests;

/// <summary>
/// One PostgreSQL instance per test run (xUnit v3 assembly fixture). Uses Testcontainers with the official
/// <c>postgres</c> image (multi-arch, so the real engine runs natively on ARM64 and x64) unless
/// <c>VELQUERY_TEST_POSTGRES</c> supplies a connection string to an existing server.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string ConnectionStringVariable = "VELQUERY_TEST_POSTGRES";
    public const string ImageVariable = "VELQUERY_TEST_POSTGRES_IMAGE";
    public const string DefaultImage = "postgres:17-alpine";
    public const int LargeRowCount = 100_000;

    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public string Source { get; private set; } = string.Empty;

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
            _container = new PostgreSqlBuilder(Source).WithDatabase("velquery_it").WithPassword("velquery_it").Build();
            await _container.StartAsync(TestContext.Current.CancellationToken);
            serverConnectionString = _container.GetConnectionString();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(serverConnectionString)
        {
            Pooling = true,
            ApplicationName = "VelQuery.IntegrationTests",
        }.ConnectionString;

        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(Schema, connection);
            await command.ExecuteNonQueryAsync();
        }

        StartupTime = stopwatch.Elapsed;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_container is not null)
        {
            await _container.StopAsync();
            await _container.DisposeAsync();
        }
    }

    public string ConnectionStringWith(Action<NpgsqlConnectionStringBuilder> configure)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        configure(builder);
        return builder.ConnectionString;
    }

    public VelQueryDataSource CreateDataSource(VelQueryOptions? options = null)
        => VelQueryPostgreSql.CreateDataSource(ConnectionString, options ?? new VelQueryOptions { AllowDynamicFallback = true });

    // Idempotent so an external server can be reused across runs.
    private const string Schema = """
        DROP TABLE IF EXISTS rows100, rows100k, types, victims CASCADE;
        DROP PROCEDURE IF EXISTS double_it(int);
        DROP PROCEDURE IF EXISTS insert_victim(int, text);

        CREATE TABLE rows100 (id bigint PRIMARY KEY, name text NOT NULL, category int NULL);
        INSERT INTO rows100 SELECT n, 'row-' || n, CASE WHEN n % 10 = 0 THEN NULL ELSE n % 3 END FROM generate_series(1, 100) n;

        CREATE TABLE rows100k (id bigint PRIMARY KEY, name text NOT NULL, payload text NOT NULL);
        INSERT INTO rows100k SELECT n, 'name-' || n, repeat('x', 150) FROM generate_series(1, 100000) n;

        CREATE TABLE types (
            id int PRIMARY KEY,
            i2 smallint, i4 int, i8 bigint, num numeric(28, 8), flag boolean, txt text, uid uuid,
            ts timestamp, tstz timestamptz, d date, tm time, bytes bytea);
        INSERT INTO types VALUES
            (1, 32767, 2147483647, 9223372036854775807, 12345678901234567890.12345678, true,
             '💡 unicode ✓ ' || repeat('é', 5000), 'a3c1d865-9684-06a4-f000-000000000001',
             '2026-10-11 08:30:15.123456', '2026-10-11 08:30:15.123456+05:30', '2026-10-11', '13:45:07.5',
             '\x00ff10'),
            (2, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);

        CREATE TABLE victims (id int PRIMARY KEY, name text NOT NULL);
        INSERT INTO victims VALUES (1, 'alice'), (2, 'bob');

        CREATE PROCEDURE double_it(INOUT v int) LANGUAGE plpgsql AS $$ BEGIN v := v * 2; END $$;
        CREATE PROCEDURE insert_victim(p_id int, p_name text) LANGUAGE sql AS $$ INSERT INTO victims VALUES (p_id, p_name) $$;
        """;
}

/// <summary>Base for tests sharing the assembly-wide PostgreSQL fixture.</summary>
public abstract class IntegrationTest(PostgreSqlFixture fixture)
{
    protected PostgreSqlFixture Fixture { get; } = fixture;

    protected static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

    protected VelQuerySession Session(VelQueryOptions? options = null) => Fixture.CreateDataSource(options).CreateSession();

    /// <summary>A data source with its own small pool so leaks surface as pool timeouts.</summary>
    protected VelQueryDataSource IsolatedPoolDataSource(int maxPoolSize, string poolTag)
        => VelQueryPostgreSql.CreateDataSource(Fixture.ConnectionStringWith(b =>
        {
            b.MaxPoolSize = maxPoolSize;
            b.Timeout = 5;
            b.ApplicationName = "VelQuery.It." + poolTag + "." + Guid.NewGuid().ToString("N")[..8];
        }));

    protected async Task<string> CreateTableAsync(string columns)
    {
        var table = "t_" + Guid.NewGuid().ToString("N");
        await using var session = Session();
        await session.ExecuteAsync($"CREATE TABLE {table} ({columns})", cancellationToken: Ct);
        return table;
    }
}

public sealed record IdName(long Id, string Name);
