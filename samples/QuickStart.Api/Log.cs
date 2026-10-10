using VelQuery;

namespace QuickStart.Api;

internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Information, "Waiting for SQL Server (attempt {Attempt}/{MaxAttempts})...")]
    public static partial void WaitingForServer(ILogger logger, int attempt, int maxAttempts);

    [LoggerMessage(2, LogLevel.Information, "Seeded dbo.Products with sample rows.")]
    public static partial void Seeded(ILogger logger);

    [LoggerMessage(3, LogLevel.Error, "Query {QueryId} failed ({Category}).")]
    public static partial void QueryFailed(ILogger logger, Exception exception, string? queryId, VelQueryErrorCategory category);
}
