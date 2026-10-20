using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace VelQuery.MongoDB;

/// <summary>Dependency-injection helpers for MongoDB.</summary>
public static class VelQueryMongoServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="VelQueryMongoDataSource"/> that owns a client built from
    /// <paramref name="connectionString"/>, and a scoped <see cref="VelQueryMongoSession"/>.
    /// </summary>
    public static IServiceCollection AddVelQueryMongo(this IServiceCollection services, string connectionString, string? databaseName = null, Action<VelQueryMongoOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var options = new VelQueryMongoOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(_ => VelQueryMongoDataSource.Create(connectionString, databaseName, options));
        services.TryAddScoped(sp => sp.GetRequiredService<VelQueryMongoDataSource>().CreateSession());
        return services;
    }

    /// <summary>
    /// Registers a singleton <see cref="VelQueryMongoDataSource"/> over an application-owned <see cref="IMongoClient"/>
    /// resolved from the container (VelQuery never disposes it), and a scoped <see cref="VelQueryMongoSession"/>.
    /// </summary>
    public static IServiceCollection AddVelQueryMongo(this IServiceCollection services, Func<IServiceProvider, IMongoClient> clientFactory, string databaseName, Action<VelQueryMongoOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        var options = new VelQueryMongoOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(sp => new VelQueryMongoDataSource(clientFactory(sp), databaseName, options));
        services.TryAddScoped(sp => sp.GetRequiredService<VelQueryMongoDataSource>().CreateSession());
        return services;
    }
}
