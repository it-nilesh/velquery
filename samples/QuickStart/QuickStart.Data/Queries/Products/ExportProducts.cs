using QuickStart.Data.Models;
using VelQuery;

namespace QuickStart.Data.Queries.Products;

// IAsyncEnumerable<T> streams rows one at a time; the reader and connection are released when enumeration ends,
// including when the consumer stops early.
[VelQuerySql("products.export", """
    SELECT Id, Name, Description, Price, Stock, CreatedAt
    FROM dbo.Products
    ORDER BY Id
    """)]
internal static partial class ExportProducts
{
    public static partial IAsyncEnumerable<Product> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}
