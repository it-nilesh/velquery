using QuickStart.Data.Models;
using VelQuery;

namespace QuickStart.Data.Queries.Products;

[VelQuerySql("products.list", """
    SELECT Id, Name, Description, Price, Stock, CreatedAt
    FROM dbo.Products
    ORDER BY Id
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY
    """)]
internal static partial class ListProducts
{
    public static partial Task<IReadOnlyList<Product>> ExecuteAsync(IVelQuerySession session, int skip, int take, CancellationToken cancellationToken = default);
}
