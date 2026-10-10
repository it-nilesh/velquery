using System.Data;
using QuickStart.Data.Models;
using VelQuery;

namespace QuickStart.Data.Queries.Products;

// OUTPUT returns the inserted row (including the identity and default timestamp) in the same round trip.
[VelQuerySql("products.create", """
    INSERT INTO dbo.Products (Name, Description, Price, Stock)
    OUTPUT INSERTED.Id, INSERTED.Name, INSERTED.Description, INSERTED.Price, INSERTED.Stock, INSERTED.CreatedAt
    VALUES (@Name, @Description, @Price, @Stock)
    """)]
internal static partial class CreateProduct
{
    public static partial Task<Product> ExecuteAsync(
        IVelQuerySession session,
        [VelQueryParameter(DbType = DbType.String, Size = ProductInput.MaxNameLength)] string name,
        [VelQueryParameter(DbType = DbType.String, Size = ProductInput.MaxDescriptionLength)] string? description,
        [VelQueryParameter(Precision = 18, Scale = 2)] decimal price,
        int stock,
        CancellationToken cancellationToken = default);
}
