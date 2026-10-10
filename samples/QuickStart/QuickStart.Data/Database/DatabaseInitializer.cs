using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using QuickStart.Data.Logging;
using VelQuery;
using VelQuery.SqlServer;

namespace QuickStart.Data.Database;

/// <summary>
/// Creates the sample database and runs the embedded scripts in <c>Database/Scripts</c> (in name order) on startup.
/// Scripts are idempotent. This is sample convenience only; real applications should use a migration tool.
/// </summary>
public static class DatabaseInitializer
{
    public const string DatabaseName = "VelQuerySample";

    private const string ScriptPrefix = "QuickStart.Data.Database.Scripts.";

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

        await using var dataSource = VelQuerySqlServer.CreateDataSource(connectionString);
        await using var appSession = dataSource.CreateSession();
        foreach (var (name, sql) in LoadScripts())
        {
            await appSession.Sql(sql).WithQueryId("schema." + name).ExecuteAsync(cancellationToken);
            Log.ScriptApplied(logger, name);
        }
    }

    /// <summary>Reads the trusted, build-time embedded scripts. They contain no user input.</summary>
    private static IEnumerable<(string Name, string Sql)> LoadScripts()
    {
        var assembly = typeof(DatabaseInitializer).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ScriptPrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (resource[ScriptPrefix.Length..^".sql".Length], reader.ReadToEnd());
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
