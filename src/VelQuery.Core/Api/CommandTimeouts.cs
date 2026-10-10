using System;
using System.Threading;

namespace VelQuery;

internal static class CommandTimeouts
{
    public static void Validate(TimeSpan timeout, string? queryId)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new VelQueryValidationException(queryId, "command timeout must be positive or Timeout.InfiniteTimeSpan.");
        }
    }

    /// <summary>Converts to ADO.NET seconds, where 0 means no timeout. Partial seconds round up.</summary>
    public static int ToSeconds(TimeSpan timeout, string? queryId)
    {
        Validate(timeout, queryId);
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return 0;
        }

        var seconds = Math.Ceiling(timeout.TotalSeconds);
        return seconds >= int.MaxValue ? 0 : (int)seconds;
    }
}
