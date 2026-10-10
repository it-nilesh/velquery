using Microsoft.Data.SqlClient;
using VelQuery;
using VelQuery.SqlServer;

namespace QuickStart.Api.Data;

/// <summary>
/// Creates the sample database, table and seed rows on startup. Schema setup is sample convenience only; real
/// applications should use a migration tool.
/// </summary>
internal static class DatabaseInitializer
{
    public const string DatabaseName = "VelQuerySample";

    public static async Task EnsureCreatedAsync(string connectionString, ILogger logger, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!string.Equals(builder.InitialCatalog, DatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The sample expects 'Database={DatabaseName}' in the connection string.");
        }

        builder.InitialCatalog = "master";
        await using (var master = VelQuerySqlServer.CreateDataSource(builder.ConnectionString))
        {
            await WaitForServerAsync(master, logger, cancellationToken);
            await using var session = master.CreateSession();
            await session.ExecuteAsync($"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}]", cancellationToken: cancellationToken);
        }

        await using var app = VelQuerySqlServer.CreateDataSource(connectionString);
        await using var appSession = app.CreateSession();
        await appSession.ExecuteAsync("""
            IF OBJECT_ID(N'dbo.Products') IS NULL
            CREATE TABLE dbo.Products
            (
                Id           int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
                Name         nvarchar(200)  NOT NULL CONSTRAINT UQ_Products_Name UNIQUE,
                Description  nvarchar(1000) NULL,
                Price        decimal(18, 2) NOT NULL CONSTRAINT CK_Products_Price CHECK (Price >= 0),
                Stock        int            NOT NULL CONSTRAINT CK_Products_Stock CHECK (Stock >= 0),
                CreatedAt    datetimeoffset(3) NOT NULL CONSTRAINT DF_Products_CreatedAt DEFAULT SYSDATETIMEOFFSET()
            );
            """, cancellationToken: cancellationToken);

        var count = await appSession.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Products", cancellationToken: cancellationToken);
        if (count == 0)
        {
            await appSession.ExecuteAsync("""
                INSERT INTO dbo.Products (Name, Description, Price, Stock) VALUES
                    (N'Mechanical keyboard', N'Tactile switches, 87 keys', 129.00, 25),
                    (N'USB-C dock', N'Dual 4K output, 100 W pass-through', 189.50, 12),
                    (N'Noise-cancelling headphones', NULL, 249.99, 7);
                """, cancellationToken: cancellationToken);
            Log.Seeded(logger);
        }
    }

    /// <summary>
    /// A freshly started container needs a few seconds before it accepts logins. This explicit, bounded startup wait is
    /// application policy — VelQuery itself never retries.
    /// </summary>
    private static async Task WaitForServerAsync(VelQueryDataSource master, ILogger logger, CancellationToken cancellationToken)
    {
        const int maxAttempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var session = master.CreateSession();
                await session.ExecuteScalarAsync<int>("SELECT 1", cancellationToken: cancellationToken);
                return;
            }
            catch (VelQueryProviderException) when (attempt < maxAttempts)
            {
                Log.WaitingForServer(logger, attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }
}
