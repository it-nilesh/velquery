using QuickStart.Data.Models;
using VelQuery;

namespace QuickStart.Data.Queries.Products;

// Task<Product?> with the default cardinality means SingleOrDefault: null for no row, an error for more than one.
[VelQuerySql("products.by-id", """
    SELECT Id, Name, Description, Price, Stock, CreatedAt
    FROM dbo.Products
    WHERE Id = @Id
    """)]
internal static partial class GetProduct
{
    public static partial Task<Product?> ExecuteAsync(IVelQuerySession session, int id, CancellationToken cancellationToken = default);
}
