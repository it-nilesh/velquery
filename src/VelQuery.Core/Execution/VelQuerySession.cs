using System;
using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Execution;
using VelQuery.Mapping;
using VelQuery.Providers;

namespace VelQuery;

/// <summary>
/// Executes queries against one database. A session created from <see cref="VelQueryDataSource.CreateSession()"/>
/// acquires a pooled connection per operation; a session over a caller-owned connection, or with an active
/// <see cref="VelQueryTransaction"/>, runs every operation on that single pinned connection.
/// </summary>
/// <remarks>
/// Sessions are not thread-safe. While a connection is pinned, starting a second operation before the first has
/// completed (including an undisposed stream or multi-result reader) throws <see cref="VelQueryValidationException"/>.
/// </remarks>
public sealed partial class VelQuerySession : IVelQuerySession
{
    private readonly VelQueryDataSource _dataSource;
    private readonly IVelQueryConnectionFactory? _connectionFactory;
    private readonly IVelQueryProvider _provider;
    private readonly ResolvedOptions _options;
    private readonly bool _callerOwnsTransaction;

    private DbConnection? _pinnedConnection;
    private bool _pinnedConnectionOwned;
    private DbTransaction? _transaction;
    private VelQueryTransaction? _transactionScope;
    private int _busy;
    private bool _disposed;

    internal VelQuerySession(VelQueryDataSource dataSource, IVelQueryConnectionFactory connectionFactory)
    {
        _dataSource = dataSource;
        _connectionFactory = connectionFactory;
        _provider = dataSource.Provider;
        _options = dataSource.Options;
    }

    internal VelQuerySession(VelQueryDataSource dataSource, DbConnection callerConnection, DbTransaction? callerTransaction)
    {
        _dataSource = dataSource;
        _provider = dataSource.Provider;
        _options = dataSource.Options;
        _pinnedConnection = callerConnection;
        _transaction = callerTransaction;
        _callerOwnsTransaction = callerTransaction is not null;
    }

    /// <summary>
    /// Session-scoped default command timeout, overriding <see cref="VelQueryOptions.DefaultTimeout"/>. Query
    /// descriptors and per-call options take precedence.
    /// </summary>
    public TimeSpan? CommandTimeout
    {
        get;
        set
        {
            if (value is { } timeout)
            {
                CommandTimeouts.Validate(timeout, queryId: null);
            }

            field = value;
        }
    }

    /// <summary>The provider used by this session.</summary>
    public IVelQueryProvider Provider => _provider;

    /// <summary>The active transaction, whether caller-owned or started by <see cref="BeginTransactionAsync"/>.</summary>
    public DbTransaction? Transaction => _transaction;

    /// <summary>Creates a runtime SQL command with per-call options.</summary>
    public VelQueryTextCommand Sql(string sql, QueryParameters? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return new VelQueryTextCommand(this, sql, parameters);
    }

    /// <summary>
    /// Begins a transaction. For factory-owned sessions this opens and pins a connection until the returned scope is
    /// disposed. Disposing the scope without committing rolls back (VQ-FR-012).
    /// </summary>
    public async ValueTask<VelQueryTransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.Unspecified, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_transaction is not null)
        {
            throw new VelQueryTransactionException(null, _callerOwnsTransaction
                ? "the session is bound to a caller-owned transaction."
                : "a transaction is already active on this session.");
        }

        EnterPinned();
        var openedConnection = false;
        try
        {
            if (_pinnedConnection is null)
            {
                _pinnedConnection = await _connectionFactory!.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                _pinnedConnectionOwned = true;
                openedConnection = true;
            }

            var transaction = await _pinnedConnection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
            _transaction = transaction;
            _transactionScope = new VelQueryTransaction(this, transaction);
            return _transactionScope;
        }
        catch (Exception ex)
        {
            if (openedConnection)
            {
                await UnpinOwnedConnectionAsync().ConfigureAwait(false);
            }

            if (ex is DbException && !cancellationToken.IsCancellationRequested)
            {
                throw new VelQueryTransactionException(null, "the transaction could not be started.", ex);
            }

            throw;
        }
        finally
        {
            ExitPinned();
        }
    }

    internal async ValueTask OnTransactionEndedAsync(VelQueryTransaction scope)
    {
        if (!ReferenceEquals(_transactionScope, scope))
        {
            return;
        }

        _transactionScope = null;
        _transaction = null;
        if (_pinnedConnectionOwned)
        {
            await UnpinOwnedConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes the session. An uncommitted session transaction is rolled back and any VelQuery-owned connection is
    /// released. Caller-owned connections and transactions are never touched.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_transactionScope is { } scope)
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }

        if (_pinnedConnectionOwned)
        {
            await UnpinOwnedConnectionAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask UnpinOwnedConnectionAsync()
    {
        var connection = _pinnedConnection;
        _pinnedConnection = null;
        _pinnedConnectionOwned = false;
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void EnterPinned()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new VelQueryValidationException(null,
                "the session is already executing an operation on its connection. Sessions are not thread-safe; complete or dispose the active operation (including streams and multi-result readers) first.");
        }
    }

    private void ExitPinned() => Volatile.Write(ref _busy, 0);

    internal async ValueTask<ConnectionLease> AcquireAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_pinnedConnection is { } pinned)
        {
            EnterPinned();
            return new ConnectionLease(pinned, owned: false, pinned: true);
        }

        var connection = await _connectionFactory!.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new ConnectionLease(connection, owned: true, pinned: false);
    }

    internal async ValueTask ReleaseAsync(ConnectionLease lease)
    {
        if (lease.Pinned)
        {
            ExitPinned();
        }

        if (lease.Owned && lease.Connection is not null)
        {
            await lease.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal DbCommand CreateCommand<TSpec>(DbConnection connection, in TSpec spec)
        where TSpec : struct, ICommandSpec
    {
        var command = connection.CreateCommand();
        try
        {
            command.CommandText = spec.Sql;
            command.CommandType = spec.CommandType;
            if ((spec.Timeout ?? CommandTimeout ?? _options.DefaultTimeout) is { } timeout)
            {
                command.CommandTimeout = CommandTimeouts.ToSeconds(timeout, spec.QueryId);
            }

            command.Transaction = _transaction;
            spec.Bind(command, _provider);
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    internal MappingContext MappingContext(string queryId) => new(queryId, _options.StrictMapping);

    internal bool StrictNulls => _options.StrictMapping;

    internal IRowMapperFactory<T> ResolveMapper<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(ReflectionRowMapperFactory.RequiredMembers)] T>(string queryId)
    {
        if (RowMappers<T>.Factory is { } factory)
        {
            return factory;
        }

        if (_options.AllowDynamicFallback)
        {
            return ReflectionRowMapperFactory<T>.Instance;
        }

        throw new VelQueryUnsupportedCapabilityException(queryId,
            $"no generated row mapper is registered for '{typeof(T).Name}' and dynamic fallback is disabled. Declare a [VelQuerySql] contract, " +
            "register a mapper with RowMappers<T>.Register, or set VelQueryOptions.AllowDynamicFallback = true.");
    }

    /// <summary>
    /// Translates a failure into the VelQuery error contract and rethrows, preserving the original stack when the
    /// exception is passed through (VQ-FR-026).
    /// </summary>
    internal Exception Translate(Exception exception, string queryId, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case VelQueryException:
                return Rethrow(exception);
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                return Rethrow(exception);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException($"Query '{queryId}' was cancelled.", exception, cancellationToken);
        }

        return _provider.Classify(exception) switch
        {
            ProviderErrorKind.Timeout => new VelQueryTimeoutException(queryId, exception),
            ProviderErrorKind.Cancelled => new OperationCanceledException($"Query '{queryId}' was cancelled by the provider.", exception),
            _ when exception is DbException => new VelQueryProviderException(queryId, exception),
            _ => Rethrow(exception),
        };
    }

    private static Exception Rethrow(Exception exception)
    {
        ExceptionDispatchInfo.Capture(exception).Throw();
        return exception;
    }

    /// <summary>
    /// Releases reader, command and connection in order. When <paramref name="faulted"/> is true, cleanup failures are
    /// suppressed so they cannot hide the initiating failure (PRD §9.1 correctness invariant).
    /// </summary>
    internal async ValueTask CleanupAsync(DbDataReader? reader, DbCommand? command, ConnectionLease lease, bool faulted)
    {
        Exception? cleanupFailure = null;
        if (reader is not null)
        {
            if (faulted && command is not null)
            {
                // Cancel before disposing so the driver does not drain remaining rows from the server.
                try
                {
                    command.Cancel();
                }
                catch (Exception ex) when (ex is DbException or InvalidOperationException)
                {
                }
            }

            try
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupFailure ??= ex;
            }
        }

        if (command is not null)
        {
            try
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupFailure ??= ex;
            }
        }

        try
        {
            await ReleaseAsync(lease).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            cleanupFailure ??= ex;
        }

        if (cleanupFailure is not null && !faulted)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }
}

internal readonly struct ConnectionLease(DbConnection connection, bool owned, bool pinned)
{
    public DbConnection Connection { get; } = connection;

    public bool Owned { get; } = owned;

    public bool Pinned { get; } = pinned;
}
