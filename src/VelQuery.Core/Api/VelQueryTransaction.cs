using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace VelQuery;

/// <summary>
/// A session-scoped transaction started by <see cref="VelQuerySession.BeginTransactionAsync"/>. Commit is explicit;
/// disposing without a successful commit rolls back and releases the pinned connection (VQ-FR-012).
/// </summary>
public sealed class VelQueryTransaction : IAsyncDisposable
{
    private readonly VelQuerySession _session;
    private readonly DbTransaction _transaction;
    private bool _completed;
    private bool _disposed;

    internal VelQueryTransaction(VelQuerySession session, DbTransaction transaction)
    {
        _session = session;
        _transaction = transaction;
    }

    /// <summary>The underlying provider transaction.</summary>
    public DbTransaction DbTransaction => _transaction;

    /// <summary>Commits the transaction. A failed commit leaves the outcome to the database; the scope is completed.</summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        _completed = true;
        try
        {
            await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new VelQueryTransactionException(null, "commit failed; the database determines whether changes were applied.", ex);
        }
    }

    /// <summary>Rolls back the transaction.</summary>
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        _completed = true;
        try
        {
            await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new VelQueryTransactionException(null, "rollback failed.", ex);
        }
    }

    /// <summary>Rolls back if not completed, then releases the transaction and any pinned VelQuery-owned connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            // DbTransaction.DisposeAsync rolls back an uncommitted transaction.
            await _transaction.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _session.OnTransactionEndedAsync(this).ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous rollback-and-release used by <see cref="VelQuerySession.Dispose"/>.</summary>
    internal void DisposeWithoutSession()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transaction.Dispose();
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new VelQueryTransactionException(null, "the transaction has already been committed or rolled back.");
        }
    }
}
