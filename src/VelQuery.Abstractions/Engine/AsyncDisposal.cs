using System;
using System.Threading.Tasks;

namespace VelQuery.Engine;

/// <summary>
/// Cleanup helpers implementing the PRD §9.1 invariant for every engine: each resource is released exactly once, and a
/// cleanup failure never hides the failure that started the cleanup.
/// </summary>
public static class AsyncDisposal
{
    /// <summary>
    /// Disposes <paramref name="resource"/> (if any) and returns the disposal failure instead of throwing, so callers can
    /// release every resource and then decide whether the first cleanup failure matters.
    /// </summary>
    public static async ValueTask<Exception?> TryDisposeAsync(IAsyncDisposable? resource)
    {
        if (resource is null)
        {
            return null;
        }

        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
