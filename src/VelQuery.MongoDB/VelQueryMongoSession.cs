using System;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using VelQuery.Engine;

namespace VelQuery.MongoDB;

/// <summary>
/// A unit of work against MongoDB (ADR-0019). Not thread-safe; create one per logical operation or request scope.
/// </summary>
/// <remarks>
/// <para><b>Ownership.</b> By default each operation runs on its own pooled connection with no driver session, like
/// VelQuery's per-operation SQL connections. <see cref="BeginTransactionAsync"/> starts a VelQuery-owned driver session
/// and multi-document transaction that every operation on this session then uses until the transaction is completed;
/// VelQuery ends that driver session when the transaction is disposed. A session created over an application-owned
/// <see cref="IClientSessionHandle"/> runs every operation in it and never commits, aborts or ends it.</para>
/// <para><b>Overlap.</b> While operations share a driver session (transaction or application session), overlapping
/// operations, including open streams, fail fast with <see cref="VelQueryValidationException"/> (<see cref="SessionGuard"/>).</para>
/// <para><b>Time limits.</b> A contract's <c>TimeoutMilliseconds</c>, or <see cref="VelQueryMongoOptions.DefaultTimeout"/>,
/// becomes the server-side <c>maxTimeMS</c> of finds, counts, aggregations and the <c>insert</c>/<c>update</c>/<c>delete</c>
/// write commands, plus a client-side deadline (<see cref="VelQueryMongoOptions.ClientDeadlineGrace"/> later) as a
/// backstop for an unresponsive server. Streams get only the server-side limit, which counts server work rather than
/// the time the consumer spends between items. When the server stops a write at its limit, documents written before
/// that point (for example earlier batches of a bulk insert outside a transaction) stay written. Either kind of expiry
/// raises <see cref="VelQueryTimeoutException"/>.</para>
/// <para><b>Errors.</b> Driver failures become <see cref="VelQueryProviderException"/> with the driver exception as
/// <see cref="Exception.InnerException"/> (so codes such as duplicate key 11000 stay inspectable); mapping failures are
/// <see cref="VelQueryMappingException"/>; caller cancellation is <see cref="OperationCanceledException"/>. Nothing is
/// retried by VelQuery.</para>
/// </remarks>
public sealed partial class VelQueryMongoSession : IAsyncDisposable, IDisposable
{
    private readonly VelQueryMongoDataSource _dataSource;
    private readonly IClientSessionHandle? _applicationSession;
    private readonly SessionGuard _guard = new();
    private VelQueryMongoTransaction? _transaction;
    private bool _disposed;

    internal VelQueryMongoSession(VelQueryMongoDataSource dataSource, IClientSessionHandle? applicationSession)
    {
        _dataSource = dataSource;
        _applicationSession = applicationSession;
    }

    /// <summary>The data source this session belongs to.</summary>
    public VelQueryMongoDataSource DataSource => _dataSource;

    /// <summary>The transaction started by <see cref="BeginTransactionAsync"/> and not yet disposed, if any.</summary>
    public VelQueryMongoTransaction? Transaction => _transaction;

    /// <summary>
    /// The driver session operations currently run in: the active transaction's, the application-owned one, or
    /// <see langword="null"/> when operations run without a driver session.
    /// </summary>
    public IClientSessionHandle? ClientSession => _transaction is { IsCompleted: false } transaction ? transaction.ClientSession : _applicationSession;

    /// <summary>
    /// Starts a VelQuery-owned driver session with a multi-document transaction (requires a replica set or sharded
    /// cluster). Commit explicitly; disposing the returned scope without committing aborts the transaction.
    /// </summary>
    public async Task<VelQueryMongoTransaction> BeginTransactionAsync(TransactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_applicationSession is not null)
        {
            throw new VelQueryTransactionException(null, "this session runs in an application-owned client session; start and complete transactions on that IClientSessionHandle.");
        }

        if (_transaction is not null)
        {
            throw new VelQueryTransactionException(null, "a transaction was already started on this session; dispose it before starting another.");
        }

        IClientSessionHandle? handle = null;
        try
        {
            handle = await _dataSource.Client.StartSessionAsync(null, cancellationToken).ConfigureAwait(false);
            handle.StartTransaction(options);
        }
        catch (Exception ex)
        {
            handle?.Dispose();
            if (ex is MongoException or NotSupportedException or TimeoutException && !cancellationToken.IsCancellationRequested)
            {
                throw new VelQueryTransactionException(null, "the transaction could not be started; MongoDB transactions require a replica set or sharded cluster.", ex);
            }

            throw;
        }

        var transaction = new VelQueryMongoTransaction(this, handle);
        _transaction = transaction;
        return transaction;
    }

    /// <summary>Aborts and releases an active transaction, if any. Never touches an application-owned session.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var transaction = _transaction;
        _transaction = null;
        if (transaction is not null)
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous variant of <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var transaction = _transaction;
        _transaction = null;
        transaction?.DisposeSynchronously();
    }

    internal void OnTransactionEnded(VelQueryMongoTransaction transaction)
    {
        if (ReferenceEquals(_transaction, transaction))
        {
            _transaction = null;
        }
    }

    internal void EnterGuard() => _guard.Enter();

    internal void ExitGuard() => _guard.Exit();

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Starts an operation: resolves the driver session, enters the guard when it is shared, and sets up limits.</summary>
    private Operation Begin(MongoOperationDescriptor descriptor, OperationKind kind, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var session = ClientSession;
        if (session is not null)
        {
            _guard.Enter();
        }

        try
        {
            var limit = descriptor.Timeout ?? _dataSource.Options.DefaultTimeout;
            CancellationTokenSource? deadline = null;
            if (limit is { } value && kind != OperationKind.Stream)
            {
                deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // The server enforces the limit (maxTimeMS on reads and write commands); the client deadline is a
                // backstop for an unresponsive server, so it fires a grace period later.
                deadline.CancelAfter(value + _dataSource.Options.ClientDeadlineGrace);
            }

            return new Operation(session is null ? null : _guard, session, deadline, limit, deadline?.Token ?? cancellationToken);
        }
        catch
        {
            if (session is not null)
            {
                _guard.Exit();
            }

            throw;
        }
    }

    private static Exception Translate(Exception exception, string queryId, Operation operation, CancellationToken cancellationToken)
    {
        // Our own deadline fired (not the caller's token): report a timeout, keeping the driver exception as inner.
        if (operation.Deadline is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested && exception is not VelQueryException)
        {
            return new VelQueryTimeoutException(queryId, exception);
        }

        return VelQueryErrors.Translate(exception, queryId, MongoErrorClassifier.Instance, cancellationToken);
    }

    private enum OperationKind
    {
        Read,
        Stream,
        Write,
    }

    /// <summary>Per-operation state; disposing releases the guard and the deadline exactly once per operation.</summary>
    private readonly struct Operation(SessionGuard? guard, IClientSessionHandle? session, CancellationTokenSource? deadline, TimeSpan? maxTime, CancellationToken token) : IDisposable
    {
        public IClientSessionHandle? Session { get; } = session;

        public CancellationTokenSource? Deadline { get; } = deadline;

        public CancellationToken Token { get; } = token;

        public TimeSpan? MaxTime { get; } = maxTime;

        public void Dispose()
        {
            Deadline?.Dispose();
            guard?.Exit();
        }
    }
}
