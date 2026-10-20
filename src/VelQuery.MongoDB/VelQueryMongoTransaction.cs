using System;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using VelQuery.Engine;

namespace VelQuery.MongoDB;

/// <summary>
/// A multi-document transaction on a VelQuery-owned driver session, started by
/// <see cref="VelQueryMongoSession.BeginTransactionAsync"/> (VQ-FR-012). Commit is explicit; disposing without a
/// successful commit aborts. Disposal always ends the driver session.
/// </summary>
/// <remarks>
/// MongoDB.Driver retries <c>commitTransaction</c> and <c>abortTransaction</c> once on retryable errors, as the MongoDB
/// transactions specification mandates; VelQuery adds no retries and does not run the "retry the whole transaction on
/// TransientTransactionError" loop. Inspect the inner exception's error labels to decide whether to retry.
/// </remarks>
public sealed class VelQueryMongoTransaction : IVelQueryTransactionScope
{
    private readonly VelQueryMongoSession _session;
    private bool _completed;
    private bool _disposed;

    internal VelQueryMongoTransaction(VelQueryMongoSession session, IClientSessionHandle clientSession)
    {
        _session = session;
        ClientSession = clientSession;
    }

    /// <summary>The VelQuery-owned driver session. Do not commit, abort or dispose it directly.</summary>
    public IClientSessionHandle ClientSession { get; }

    /// <summary>True once <see cref="CommitAsync"/> or <see cref="RollbackAsync"/> has been called.</summary>
    public bool IsCompleted => _completed;

    /// <summary>Commits. A failed commit leaves the outcome to the server; the scope is completed either way.</summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        _session.EnterGuard();
        _completed = true;
        try
        {
            await ClientSession.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            throw new VelQueryTransactionException(null,
                "commit failed; the server determines whether the changes were applied. The inner exception's error labels (for example UnknownTransactionCommitResult or TransientTransactionError) describe the outcome.",
                ex);
        }
        finally
        {
            _session.ExitGuard();
        }
    }

    /// <summary>Aborts the transaction.</summary>
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        _session.EnterGuard();
        _completed = true;
        try
        {
            await ClientSession.AbortTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            throw new VelQueryTransactionException(null, "abort failed; the server aborts the transaction when the session ends or the transaction times out.", ex);
        }
        finally
        {
            _session.ExitGuard();
        }
    }

    /// <summary>Aborts if not completed, then ends the driver session.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_completed && ClientSession.IsInTransaction)
            {
                _completed = true;
                await AbortQuietlyAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            ClientSession.Dispose();
            _session.OnTransactionEnded(this);
        }
    }

    internal void DisposeSynchronously()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_completed && ClientSession.IsInTransaction)
            {
                _completed = true;
#pragma warning disable CA1031 // Disposal must not hide the failure that is usually propagating (AGENTS rule 1).
                try
                {
                    ClientSession.AbortTransaction(CancellationToken.None);
                }
                catch (Exception)
                {
                    // Ending the session below makes the server abort the transaction.
                }
#pragma warning restore CA1031
            }
        }
        finally
        {
            ClientSession.Dispose();
            _session.OnTransactionEnded(this);
        }
    }

    private async Task AbortQuietlyAsync()
    {
#pragma warning disable CA1031 // Disposal must not hide the failure that is usually propagating (AGENTS rule 1).
        try
        {
            await ClientSession.AbortTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ending the session (in DisposeAsync) makes the server abort the transaction; nothing is committed.
        }
#pragma warning restore CA1031
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
