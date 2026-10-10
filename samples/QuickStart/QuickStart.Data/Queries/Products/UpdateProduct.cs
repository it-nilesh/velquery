using System.Data;
using QuickStart.Data.Models;
using VelQuery;

namespace QuickStart.Data.Queries.Products;

[VelQuerySql("products.update", """
    UPDATE dbo.Products
    SET Name = @Name, Description = @Description, Price = @Price, Stock = @Stock
    WHERE Id = @Id
    """, Cardinality = QueryCardinality.NonQuery)]
internal static partial class UpdateProduct
{
    public static partial Task<int> ExecuteAsync(
        IVelQuerySession session,
        int id,
        [VelQueryParameter(DbType = DbType.String, Size = ProductInput.MaxNameLength)] string name,
        [VelQueryParameter(DbType = DbType.String, Size = ProductInput.MaxDescriptionLength)] string? description,
        [VelQueryParameter(Precision = 18, Scale = 2)] decimal price,
        int stock,
        CancellationToken cancellationToken = default);
}
