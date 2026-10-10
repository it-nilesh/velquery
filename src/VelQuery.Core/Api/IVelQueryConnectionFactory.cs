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
public sealed class DataSourceConnectionFactory : IVelQueryConnectionFactory
{
    private readonly DbDataSource _dataSource;

    /// <summary>Creates a factory over <paramref name="dataSource"/>. The caller retains ownership of the data source.</summary>
    public DataSourceConnectionFactory(DbDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public ValueTask<DbConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken)
        => _dataSource.OpenConnectionAsync(cancellationToken);
}
