using Microsoft.Extensions.Logging;

namespace QuickStart.Data.Logging;

internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Information, "Waiting for SQL Server (attempt {Attempt}/{MaxAttempts})...")]
    public static partial void WaitingForServer(ILogger logger, int attempt, int maxAttempts);

    [LoggerMessage(2, LogLevel.Information, "Applied database script {Script}.")]
    public static partial void ScriptApplied(ILogger logger, string script);
}
