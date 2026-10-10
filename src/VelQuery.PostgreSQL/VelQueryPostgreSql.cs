using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace VelQuery.PostgreSQL;

/// <summary>Factory and dependency-injection helpers for PostgreSQL.</summary>
public static class VelQueryPostgreSql
{
    /// <summary>
    /// Creates a data source over an <see cref="NpgsqlDataSource"/> built from <paramref name="connectionString"/>.
    /// Connections come from Npgsql's pool; VelQuery adds no pooling. Disposing the returned data source disposes the
    /// Npgsql data source.
    /// </summary>
    public static VelQueryDataSource CreateDataSource(string connectionString, VelQueryOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return CreateDataSource(NpgsqlDataSource.Create(connectionString), ownsDataSource: true, options);
    }

    /// <summary>
    /// Creates a data source over an application-configured <see cref="NpgsqlDataSource"/> (for example one built
    /// with <see cref="NpgsqlSlimDataSourceBuilder"/> for Native AOT, or with type mappings registered).
    /// </summary>
    public static VelQueryDataSource CreateDataSource(NpgsqlDataSource dataSource, bool ownsDataSource = false, VelQueryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return new VelQueryDataSource(PostgreSqlProvider.Instance, new DataSourceConnectionFactory(dataSource, ownsDataSource), options);
    }

    /// <summary>
    /// Registers a singleton <see cref="VelQueryDataSource"/> for PostgreSQL and a scoped <see cref="IVelQuerySession"/>.
    /// </summary>
    public static IServiceCollection AddVelQueryPostgreSql(this IServiceCollection services, string connectionString, Action<VelQueryOptions>? configure = null)
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
