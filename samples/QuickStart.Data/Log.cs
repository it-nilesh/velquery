using Microsoft.Extensions.Logging;

namespace QuickStart.Data;

internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Information, "Waiting for SQL Server (attempt {Attempt}/{MaxAttempts})...")]
    public static partial void WaitingForServer(ILogger logger, int attempt, int maxAttempts);

    [LoggerMessage(2, LogLevel.Information, "Seeded dbo.Products with sample rows.")]
    public static partial void Seeded(ILogger logger);
}
