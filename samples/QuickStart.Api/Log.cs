namespace QuickStart.Api;

internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Error, "Query {QueryId} failed ({Category}).")]
    public static partial void QueryFailed(ILogger logger, Exception exception, string? queryId, string? category);
}
