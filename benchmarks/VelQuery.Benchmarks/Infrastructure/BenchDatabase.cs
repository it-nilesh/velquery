using System;
using System.Data;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace VelQuery.Benchmarks.Infrastructure;

/// <summary>
/// Provides the shared benchmark database. Started once in the host process (before BenchmarkDotNet spawns the
/// per-benchmark child processes); children receive the connection string through
/// <see cref="ConnectionStringVariable"/>.
/// </summary>
public sealed class BenchDatabase : IAsyncDisposable
{
    public const string ConnectionStringVariable = "VELQUERY_BENCH_SQLSERVER";
    public const string ImageVariable = "VELQUERY_BENCH_SQLSERVER_IMAGE";

    /// <summary>
    /// SQL Server 2022 x64 images do not run under QEMU on arm64 hosts; Azure SQL Edge (SQL Server engine 15.x) is the
    /// only arm64-native SQL Server image. Results from the two images must never be compared with each other.
    /// </summary>
    public const string Arm64Image = "mcr.microsoft.com/azure-sql-edge:1.0.7";
    public const string X64Image = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    public const int RowCount = 100_000;
    public const int Seed = 20261010;

    private readonly MsSqlContainer? _container;

    private BenchDatabase(string connectionString, MsSqlContainer? container, string source)
    {
        ConnectionString = connectionString;
        _container = container;
        Source = source;
    }

    public string ConnectionString { get; }

    /// <summary>Human-readable origin of the database (container image or "external").</summary>
    public string Source { get; }

    public static async Task<BenchDatabase> StartAsync()
    {
        var external = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            return new BenchDatabase(Normalize(external), null, "external (" + ConnectionStringVariable + ")");
        }

        var image = Environment.GetEnvironmentVariable(ImageVariable);
        if (string.IsNullOrWhiteSpace(image))
        {
            image = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? Arm64Image : X64Image;
        }

        var builder = new MsSqlBuilder(image);
        if (image.Contains("azure-sql-edge", StringComparison.OrdinalIgnoreCase))
        {
            // Testcontainers.MsSql waits by running sqlcmd inside the container; Azure SQL Edge ships without it.
            builder = builder.WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("SQL Server is now ready for client connections"));
        }

        var container = builder.Build();
        await container.StartAsync();
        return new BenchDatabase(Normalize(container.GetConnectionString()), container, "testcontainers: " + image);
    }

    /// <summary>Pins pooling and disables connection-level extras so every library sees the same connection string.</summary>
    private static string Normalize(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 100,
            MultipleActiveResultSets = false,
            Encrypt = SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = true,
            ApplicationName = "velquery-bench",
        };
        if (string.Equals(builder.InitialCatalog, "master", StringComparison.OrdinalIgnoreCase) || builder.InitialCatalog.Length == 0)
        {
            builder.InitialCatalog = "VelQueryBench";
        }

        return builder.ConnectionString;
    }

    /// <summary>Creates the database and seeds <see cref="RowCount"/> deterministic rows if not already present.</summary>
    public async Task SeedAsync()
    {
        var target = new SqlConnectionStringBuilder(ConnectionString);
        var database = target.InitialCatalog;
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master", Pooling = false };

        await RetryAsync(async () =>
        {
            await using var connection = new SqlConnection(master.ConnectionString);
            await connection.OpenAsync();
            // CREATE DATABASE cannot take a parameter; the name is bound as @db and quoted server-side with QUOTENAME.
#pragma warning disable DAP212
            await using var create = new SqlCommand(
                "IF DB_ID(@db) IS NULL BEGIN DECLARE @ddl nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@db); EXEC (@ddl); END",
                connection);
#pragma warning restore DAP212
            create.Parameters.Add(new SqlParameter("@db", SqlDbType.NVarChar, 128) { Value = database });
            await create.ExecuteNonQueryAsync();
        });

        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync();
        await using (var ddl = new SqlCommand("""
            IF OBJECT_ID(N'dbo.BenchRows') IS NULL
            CREATE TABLE dbo.BenchRows (
                Id int NOT NULL CONSTRAINT PK_BenchRows PRIMARY KEY CLUSTERED,
                Name nvarchar(100) NOT NULL,
                Email nvarchar(200) NULL,
                Amount decimal(18, 4) NOT NULL,
                CreatedUtc datetime2(7) NOT NULL,
                IsActive bit NOT NULL,
                CategoryId int NOT NULL);
            """, db))
        {
            await ddl.ExecuteNonQueryAsync();
        }

        await using (var count = new SqlCommand("SELECT COUNT(*) FROM dbo.BenchRows", db))
        {
            var existing = (int)(await count.ExecuteScalarAsync())!;
            if (existing == RowCount)
            {
                return;
            }

            if (existing != 0)
            {
                await using var truncate = new SqlCommand("TRUNCATE TABLE dbo.BenchRows", db);
                await truncate.ExecuteNonQueryAsync();
            }
        }

        using var table = BuildSeedTable();
        using var bulk = new SqlBulkCopy(db) { DestinationTableName = "dbo.BenchRows", BatchSize = 10_000 };
        await bulk.WriteToServerAsync(table);

        await using var stats = new SqlCommand("UPDATE STATISTICS dbo.BenchRows WITH FULLSCAN;", db);
        await stats.ExecuteNonQueryAsync();
    }

    /// <summary>Deterministic data: fixed seed, every 10th Email NULL, mixed-length Unicode names, decimal(18,4).</summary>
    private static DataTable BuildSeedTable()
    {
        var random = new Random(Seed);
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Email", typeof(string));
        table.Columns.Add("Amount", typeof(decimal));
        table.Columns.Add("CreatedUtc", typeof(DateTime));
        table.Columns.Add("IsActive", typeof(bool));
        table.Columns.Add("CategoryId", typeof(int));
        const string alphabet = "abcdefghijklmnopqrstuvwxyzäöüßéñ";
        var chars = new char[60];
        for (var id = 1; id <= RowCount; id++)
        {
            var length = random.Next(10, 61);
            for (var i = 0; i < length; i++)
            {
                chars[i] = alphabet[random.Next(alphabet.Length)];
            }

            var name = new string(chars, 0, length);
            string? email = id % 10 == 0 ? null : $"user{id}@example.test";
            var amount = random.Next(0, 1_000_000_000) / 10_000m;
            var created = epoch.AddTicks(random.NextInt64(0, TimeSpan.TicksPerDay * 365));
            var active = random.Next(2) == 0;
            var category = random.Next(1, 51);
            table.Rows.Add(id, name, (object?)email ?? DBNull.Value, amount, created, active, category);
        }

        return table;
    }

    /// <summary>Reads server, driver and runtime versions for the environment record.</summary>
    public async Task<(string ProductVersion, string Edition, string Version)> ServerInfoAsync()
    {
        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), @@VERSION", db);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task RetryAsync(Func<Task> action)
    {
        // The "ready" log line can precede the first accepted login by a few seconds.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (SqlException) when (attempt < 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
