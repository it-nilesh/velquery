using System;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VelQuery.SqlServer;

/// <summary>Factory and dependency-injection helpers for SQL Server.</summary>
public static class VelQuerySqlServer
{
    /// <summary>
    /// Creates a data source over a SqlClient <see cref="System.Data.Common.DbDataSource"/> built from
    /// <paramref name="connectionString"/>. Connections come from SqlClient's pool; VelQuery adds no pooling.
    /// Disposing the returned data source disposes the SqlClient data source.
    /// </summary>
    public static VelQueryDataSource CreateDataSource(string connectionString, VelQueryOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var dataSource = SqlClientFactory.Instance.CreateDataSource(connectionString);
        return new VelQueryDataSource(SqlServerProvider.Instance, new DataSourceConnectionFactory(dataSource, ownsDataSource: true), options);
    }

    /// <summary>
    /// Registers a singleton <see cref="VelQueryDataSource"/> for SQL Server and a scoped <see cref="IVelQuerySession"/>.
    /// </summary>
    public static IServiceCollection AddVelQuerySqlServer(this IServiceCollection services, string connectionString, Action<VelQueryOptions>? configure = null)
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
