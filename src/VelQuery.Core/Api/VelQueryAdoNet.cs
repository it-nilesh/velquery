using System;
using System.Data.Common;
using VelQuery.Providers;

namespace VelQuery;

/// <summary>
/// Creates data sources over any ADO.NET driver through <see cref="GenericAdoNetProvider"/> (reduced guarantees; see
/// that type). Use a dedicated provider package (<c>VelQuery.SqlServer</c>, <c>VelQuery.PostgreSQL</c>, …) when one exists.
/// </summary>
public static class VelQueryAdoNet
{
    /// <summary>Creates a data source over a driver <see cref="DbDataSource"/>.</summary>
    public static VelQueryDataSource CreateDataSource(DbDataSource dataSource, GenericAdoNetProviderOptions? dialect = null,
        VelQueryOptions? options = null, bool ownsDataSource = false)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return new VelQueryDataSource(new GenericAdoNetProvider(dialect), new DataSourceConnectionFactory(dataSource, ownsDataSource), options);
    }

    /// <summary>
    /// Creates a data source from a driver's <see cref="DbProviderFactory"/> and connection string. The created
    /// <see cref="DbDataSource"/> is owned by (and disposed with) the returned data source.
    /// </summary>
    public static VelQueryDataSource CreateDataSource(DbProviderFactory factory, string connectionString,
        GenericAdoNetProviderOptions? dialect = null, VelQueryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return CreateDataSource(factory.CreateDataSource(connectionString), dialect, options, ownsDataSource: true);
    }
}
