using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using VelQuery.SqlServer.IntegrationTests;
using Xunit;

[assembly: AssemblyFixture(typeof(SqlServerFixture))]

namespace VelQuery.SqlServer.IntegrationTests;

/// <summary>
/// One SQL Server instance per test run (xUnit v3 assembly fixture). Uses Testcontainers unless
/// <c>VELQUERY_TEST_SQLSERVER</c> supplies a connection string to an existing server. Creates a dedicated database
/// with schema and seed data; the container is stopped when the run ends.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string ConnectionStringVariable = "VELQUERY_TEST_SQLSERVER";
    public const string ImageVariable = "VELQUERY_TEST_SQLSERVER_IMAGE";

    // SQL Server 2022 is amd64-only. On ARM64 hosts without Rosetta it does not start under QEMU, so the
    // ARM64-native Azure SQL Edge image (same engine/TDS surface for everything tested here) is the default there.
    public const string DefaultImage = "mcr.microsoft.com/mssql/server:2022-latest";
    public const string Arm64Image = "mcr.microsoft.com/azure-sql-edge:latest";

    public const int LargeRowCount = 100_000;
    public const int MediumRowCount = 100;

    private const string DatabaseName = "VelQueryIt";

    private MsSqlContainer? _container;

    /// <summary>Connection string (pooling enabled) to the test database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>Image used, or "external" when an existing server was supplied.</summary>
    public string Source { get; private set; } = string.Empty;

    /// <summary>Time taken to start the server and seed data.</summary>
    public TimeSpan StartupTime { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        string serverConnectionString;
        var external = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            serverConnectionString = external;
            Source = "external";
        }
        else
        {
            var image = Environment.GetEnvironmentVariable(ImageVariable);
            if (string.IsNullOrWhiteSpace(image))
            {
                image = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? Arm64Image : DefaultImage;
            }

            Source = image;
            _container = new MsSqlBuilder(image)
                .WithPassword("VelQuery_It#2026")
                // Azure SQL Edge ships without sqlcmd, so readiness is detected from the log and then by connecting.
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("SQL Server is now ready for client connections"))
                .Build();
            await _container.StartAsync(TestContext.Current.CancellationToken);
            serverConnectionString = _container.GetConnectionString();
        }

        var master = new SqlConnectionStringBuilder(serverConnectionString)
        {
            InitialCatalog = "master",
            TrustServerCertificate = true,
            Pooling = false,
        };
        await WaitForServerAsync(master.ConnectionString);
        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync();
            await ExecAsync(connection, $"IF DB_ID('{DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]; END");
            await ExecAsync(connection, $"CREATE DATABASE [{DatabaseName}]");
        }

        var builder = new SqlConnectionStringBuilder(serverConnectionString)
        {
            InitialCatalog = DatabaseName,
            TrustServerCertificate = true,
            Pooling = true,
            ApplicationName = "VelQuery.IntegrationTests",
        };
        ConnectionString = builder.ConnectionString;
        await using (var connection = new SqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await CreateSchemaAsync(connection);
        }

        StartupTime = stopwatch.Elapsed;
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        if (_container is not null)
        {
            await _container.StopAsync();
            await _container.DisposeAsync();
        }
    }

    /// <summary>A connection string to the test database with extra builder customization (e.g. pool size).</summary>
    public string ConnectionStringWith(Action<SqlConnectionStringBuilder> configure)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        configure(builder);
        return builder.ConnectionString;
    }

    public VelQueryDataSource CreateDataSource(VelQueryOptions? options = null)
        => VelQuerySqlServer.CreateDataSource(ConnectionString, options ?? new VelQueryOptions { AllowDynamicFallback = true });

    public async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task WaitForServerAsync(string connectionString)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (true)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                await ExecAsync(connection, "SELECT 1");
                return;
            }
            catch (SqlException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000);
            }
        }
    }

    internal static async Task ExecAsync(SqlConnection connection, string sql, int timeoutSeconds = 300)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Long Unicode value (&gt; 4000 UTF-16 units) mixing BMP and non-BMP (surrogate pair) characters.</summary>
    public static readonly string LongUnicode = BuildLongUnicode();

    public static readonly decimal HighPrecisionDecimal = 1234567890.123456789012345678m;

    public static readonly DateTime Precise = new DateTime(2024, 2, 29, 23, 59, 58, DateTimeKind.Unspecified).AddTicks(1234567);

    public static readonly DateTimeOffset PreciseOffset = new DateTimeOffset(2024, 2, 29, 23, 59, 58, TimeSpan.FromMinutes(330)).AddTicks(7654321);

    public static readonly Guid KnownGuid = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    public static readonly byte[] LargeBinary = BuildBinary(100_000);

    private static string BuildLongUnicode()
    {
        var sb = new StringBuilder();
        var i = 0;
        while (sb.Length < 6000)
        {
            sb.Append("Ünïcødé-");
            sb.Append("\U0001F600"); // 😀 non-BMP
            sb.Append('漢');
            sb.Append("\U0001D11E"); // 𝄞 non-BMP
            sb.Append(i.ToString(CultureInfo.InvariantCulture));
            i++;
        }

        return sb.ToString();
    }

    private static byte[] BuildBinary(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31) ^ (i >> 8));
        }

        return bytes;
    }

    private static async Task CreateSchemaAsync(SqlConnection connection)
    {
        // Row-count tables: 1, 100 and 100,000 rows. Name length grows with Id for incremental-streaming checks.
        await ExecAsync(connection, """
            CREATE TABLE dbo.Rows1 (Id bigint NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL);
            CREATE TABLE dbo.Rows100 (Id bigint NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL, Category int NULL);
            CREATE TABLE dbo.Rows100k (Id bigint NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL, Payload nvarchar(200) NOT NULL);
            INSERT dbo.Rows1 (Id, Name) VALUES (1, N'only');
            INSERT dbo.Rows100 (Id, Name, Category)
                SELECT n, CONCAT(N'row-', n), CASE WHEN n % 10 = 0 THEN NULL ELSE n % 3 END
                FROM (SELECT TOP (100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects) t;
            WITH n AS (
                SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT dbo.Rows100k (Id, Name, Payload)
                SELECT n, CONCAT(N'name-', n), REPLICATE(N'x', 150) FROM n;
            """);

        // Type-fidelity table. Row 1: all values; row 2: all NULL (nullable columns).
        await ExecAsync(connection, """
            CREATE TABLE dbo.Types (
                Id int NOT NULL PRIMARY KEY,
                LongText nvarchar(max) NULL,
                ShortText nvarchar(50) NULL,
                AnsiText varchar(50) NULL,
                Amount decimal(38,18) NULL,
                Created datetime2(7) NULL,
                CreatedOffset datetimeoffset(7) NULL,
                UniqueId uniqueidentifier NULL,
                Data varbinary(max) NULL,
                Flag bit NULL,
                Tiny tinyint NULL,
                Small smallint NULL,
                Big bigint NULL);
            """);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT dbo.Types VALUES (1, @LongText, N'short', 'ansi', @Amount, @Created, @CreatedOffset, @UniqueId, @Data, 1, 255, -32768, -9223372036854775808);
                INSERT dbo.Types (Id) VALUES (2);
                """;
            insert.Parameters.Add(new SqlParameter("@LongText", System.Data.SqlDbType.NVarChar, -1) { Value = LongUnicode });
            insert.Parameters.Add(new SqlParameter("@Amount", System.Data.SqlDbType.Decimal) { Precision = 38, Scale = 18, Value = HighPrecisionDecimal });
            insert.Parameters.Add(new SqlParameter("@Created", System.Data.SqlDbType.DateTime2) { Scale = 7, Value = Precise });
            insert.Parameters.Add(new SqlParameter("@CreatedOffset", System.Data.SqlDbType.DateTimeOffset) { Scale = 7, Value = PreciseOffset });
            insert.Parameters.Add(new SqlParameter("@UniqueId", System.Data.SqlDbType.UniqueIdentifier) { Value = KnownGuid });
            insert.Parameters.Add(new SqlParameter("@Data", System.Data.SqlDbType.VarBinary, -1) { Value = LargeBinary });
            await insert.ExecuteNonQueryAsync();
        }

        // Scratch table for writes, injection and transaction tests. Each test uses its own key range/marker.
        await ExecAsync(connection, """
            CREATE TABLE dbo.Scratch (Id int IDENTITY PRIMARY KEY, Marker nvarchar(100) NOT NULL, Value nvarchar(max) NULL);
            CREATE TABLE dbo.Victims (Id int NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL);
            INSERT dbo.Victims VALUES (1, N'alice'), (2, N'bob'), (3, N'carol');
            """);

        await ExecAsync(connection, """
            CREATE PROCEDURE dbo.ProcAll
                @Input int,
                @Doubled int OUTPUT,
                @Counter bigint OUTPUT,
                @Label nvarchar(50) OUTPUT
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT Id, Name FROM dbo.Rows100 WHERE Id <= @Input ORDER BY Id;
                SELECT COUNT(*) AS Total FROM dbo.Rows100;
                SET @Doubled = @Input * 2;
                SET @Counter = @Counter + 1;
                SET @Label = CONCAT(N'input=', @Input);
                RETURN 42;
            END
            """);
        await ExecAsync(connection, """
            CREATE PROCEDURE dbo.ProcNoRows @Value int, @Result int OUTPUT
            AS
            BEGIN
                SET @Result = @Value + 1;
                RETURN @Value * 10;
            END
            """);
        await ExecAsync(connection, """
            CREATE PROCEDURE dbo.ProcFails @Value int
            AS
            BEGIN
                SELECT @Value AS Value;
                SELECT 1 / 0 AS Boom;
            END
            """);
    }
}
