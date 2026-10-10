using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace VelQuery;

/// <summary>
/// Creates open connections for factory-owned operations. VelQuery disposes every connection it obtains from the
/// factory, returning it to the driver's pool; VelQuery never implements its own pooling.
/// </summary>
public interface IVelQueryConnectionFactory
{
    /// <summary>Creates and opens a connection.</summary>
    ValueTask<DbConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>Connection factory backed by an ADO.NET <see cref="DbDataSource"/> owned by the application.</summary>
public sealed class DataSourceConnectionFactory : IVelQueryConnectionFactory, IAsyncDisposable, IDisposable
{
    private readonly DbDataSource _dataSource;
    private readonly bool _ownsDataSource;

    /// <summary>Creates a factory over <paramref name="dataSource"/>.</summary>
    /// <param name="dataSource">The driver data source.</param>
    /// <param name="ownsDataSource">When true, disposing the factory disposes the data source; otherwise the caller keeps ownership.</param>
    public DataSourceConnectionFactory(DbDataSource dataSource, bool ownsDataSource = false)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _ownsDataSource ? _dataSource.DisposeAsync() : ValueTask.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsDataSource)
        {
            _dataSource.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask<DbConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken)
        => _dataSource.OpenConnectionAsync(cancellationToken);
}
