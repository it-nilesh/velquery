using Microsoft.Extensions.DependencyInjection;
using VelQuery.SqlServer;

namespace QuickStart.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers VelQuery for SQL Server (singleton data source over SqlClient's pool, scoped session) and a scoped
    /// <see cref="IProductRepository"/>.
    /// </summary>
    public static IServiceCollection AddProductData(this IServiceCollection services, string connectionString)
    {
        services.AddVelQuerySqlServer(connectionString, options =>
        {
            options.DefaultTimeout = TimeSpan.FromSeconds(15);
            options.StrictMapping = true;
            options.AllowDynamicFallback = false;
        });
        services.AddScoped<IProductRepository, ProductRepository>();
        return services;
    }
}
