using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using VelQuery.Providers;

namespace VelQuery;

/// <summary>
/// Entry point that combines a provider, an optional connection factory and immutable options. Thread-safe; create
/// one per database and share it.
/// </summary>
public sealed class VelQueryDataSource : IAsyncDisposable, IDisposable
{
    private readonly IVelQueryConnectionFactory? _connectionFactory;

    /// <summary>Creates a data source.</summary>
    /// <param name="provider">Dialect/driver provider.</param>
    /// <param name="connectionFactory">Factory for VelQuery-owned connections; may be null if only caller-owned connections are used.</param>
    /// <param name="options">Options, copied at construction.</param>
    public VelQueryDataSource(IVelQueryProvider provider, IVelQueryConnectionFactory? connectionFactory, VelQueryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Provider = provider;
        _connectionFactory = connectionFactory;
        Options = (options ?? new VelQueryOptions()).Resolve();
    }

    /// <summary>Disposes the connection factory if it is disposable (for example a factory that owns its driver data source).</summary>
    public ValueTask DisposeAsync()
        => _connectionFactory is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;

    /// <summary>Disposes the connection factory if it is disposable.</summary>
    public void Dispose() => (_connectionFactory as IDisposable)?.Dispose();

    /// <summary>The provider.</summary>
    public IVelQueryProvider Provider { get; }

    internal ResolvedOptions Options { get; }

    /// <summary>
    /// Creates a session whose operations each acquire and release a factory-owned connection, unless a session
    /// transaction pins one. Creating a session performs no I/O.
    /// </summary>
    public VelQuerySession CreateSession()
    {
        if (_connectionFactory is null)
        {
            throw new VelQueryValidationException(null, "this data source has no connection factory; use CreateSession(DbConnection) with a caller-owned connection.");
        }

        return new VelQuerySession(this, _connectionFactory);
    }

    /// <summary>
    /// Creates a session over a caller-owned open connection and optional caller-owned transaction. VelQuery never
    /// opens, closes or disposes the connection, and never commits or rolls back the transaction (VQ-FR-001, VQ-FR-011).
    /// </summary>
    public VelQuerySession CreateSession(DbConnection connection, DbTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open)
        {
            throw new VelQueryValidationException(null, "caller-owned connections must be open before creating a session.");
        }

        if (transaction is not null && !ReferenceEquals(transaction.Connection, connection))
        {
            throw new VelQueryValidationException(null, "the transaction does not belong to the supplied connection.");
        }

        return new VelQuerySession(this, connection, transaction);
    }
}
