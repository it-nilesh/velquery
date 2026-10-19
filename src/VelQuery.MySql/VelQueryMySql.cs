using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySqlConnector;

namespace VelQuery.MySql;

/// <summary>Factory and dependency-injection helpers for MySQL and MariaDB.</summary>
public static class VelQueryMySql
{
    /// <summary>
    /// Creates a data source over a <see cref="MySqlDataSource"/> built from <paramref name="connectionString"/>.
    /// Connections come from MySqlConnector's pool; VelQuery adds no pooling. Disposing the returned data source disposes
    /// the MySqlConnector data source. Bulk insert additionally needs <c>AllowLoadLocalInfile=true</c>.
    /// </summary>
    public static VelQueryDataSource CreateDataSource(string connectionString, VelQueryOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return CreateDataSource(new MySqlDataSource(connectionString), ownsDataSource: true, options);
    }

    /// <summary>
    /// Creates a data source over an application-configured <see cref="MySqlDataSource"/> (for example one built with
    /// <see cref="MySqlDataSourceBuilder"/> to supply credentials or certificates).
    /// </summary>
    public static VelQueryDataSource CreateDataSource(MySqlDataSource dataSource, bool ownsDataSource = false, VelQueryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return new VelQueryDataSource(MySqlProvider.Instance, new DataSourceConnectionFactory(dataSource, ownsDataSource), options);
    }

    /// <summary>
    /// Registers a singleton <see cref="VelQueryDataSource"/> for MySQL/MariaDB and a scoped <see cref="IVelQuerySession"/>.
    /// </summary>
    public static IServiceCollection AddVelQueryMySql(this IServiceCollection services, string connectionString, Action<VelQueryOptions>? configure = null)
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
