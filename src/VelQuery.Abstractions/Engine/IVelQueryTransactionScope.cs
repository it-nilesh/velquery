using System;
using System.Threading;
using System.Threading.Tasks;

namespace VelQuery.Engine;

/// <summary>
/// Engine-neutral transaction scope started by a VelQuery session (VQ-FR-012). Commit is explicit; disposing without
/// a successful commit rolls back. A scope never commits or rolls back a caller-owned transaction.
/// </summary>
public interface IVelQueryTransactionScope : IAsyncDisposable
{
    /// <summary>Commits. A failed commit leaves the outcome to the database; the scope is completed either way.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Rolls back.</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
