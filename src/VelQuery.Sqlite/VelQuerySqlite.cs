using System;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VelQuery.Sqlite;

/// <summary>Factory and dependency-injection helpers for SQLite.</summary>
/// <remarks>
/// Each VelQuery operation opens and disposes its own connection unless a session transaction or caller-owned
/// connection pins one. A plain <c>Data Source=:memory:</c> database lives only as long as one connection, so it is
/// empty for every operation; use a file, a named shared-cache memory database
/// (<c>Data Source=name;Mode=Memory;Cache=Shared</c>, kept alive by one open connection), or a caller-owned connection.
/// </remarks>
public static class VelQuerySqlite
{
    /// <summary>
    /// Creates a data source over Microsoft.Data.Sqlite for <paramref name="connectionString"/>. Connections come from
    /// Microsoft.Data.Sqlite's pool (keyed by connection string, on by default); VelQuery adds no pooling. The created
    /// driver data source is owned by the returned data source. Disposing it does not clear Microsoft.Data.Sqlite's
    /// process-wide pool, which other code may share; call <see cref="SqliteConnection.ClearPool"/> to release files.
    /// </summary>
    public static VelQueryDataSource CreateDataSource(string connectionString, VelQueryOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return CreateDataSource(SqliteFactory.Instance.CreateDataSource(connectionString), ownsDataSource: true, options);
    }

    /// <summary>
    /// Creates a data source over an application-supplied <see cref="DbDataSource"/> that produces
    /// <see cref="SqliteConnection"/>s (for example one that runs PRAGMAs when a connection opens).
    /// </summary>
    public static VelQueryDataSource CreateDataSource(DbDataSource dataSource, bool ownsDataSource = false, VelQueryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return new VelQueryDataSource(SqliteProvider.Instance, new DataSourceConnectionFactory(dataSource, ownsDataSource), options);
    }

    /// <summary>
    /// Registers a singleton <see cref="VelQueryDataSource"/> for SQLite and a scoped <see cref="IVelQuerySession"/>.
    /// </summary>
    public static IServiceCollection AddVelQuerySqlite(this IServiceCollection services, string connectionString, Action<VelQueryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var options = new VelQueryOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(_ => CreateDataSource(connectionString, options));
        services.TryAddScoped<IVelQuerySession>(sp => sp.GetRequiredService<VelQueryDataSource>().CreateSession());
        return services;
    }
}
