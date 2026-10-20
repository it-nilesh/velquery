using System.Threading;

namespace VelQuery.Engine;

/// <summary>
/// Single-operation guard for a session-owned resource (a pinned connection, a driver session). Sessions are not
/// thread-safe; overlapping operations on one pinned resource fail fast instead of corrupting driver state.
/// </summary>
public sealed class SessionGuard
{
    private int _busy;

    /// <summary>Marks the resource busy, or throws <see cref="VelQueryValidationException"/> if it already is.</summary>
    public void Enter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new VelQueryValidationException(null,
                "the session is already executing an operation on its connection. Sessions are not thread-safe; complete or dispose the active operation (including streams and multi-result readers) first.");
        }
    }

    /// <summary>Releases the resource.</summary>
    public void Exit() => Volatile.Write(ref _busy, 0);
}
