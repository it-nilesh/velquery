using System.Data;
using VelQuery;

namespace QuickStart.Data;

// Each contract is static SQL checked at build time: a misspelled @parameter is error VQ1001 and an unused method
// parameter is warning VQ1002. The generator implements every partial method. Contracts are internal so callers go
// through IProductRepository, which can be mocked.

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

[VelQuerySql("products.delete", "DELETE FROM dbo.Products WHERE Id = @Id", Cardinality = QueryCardinality.NonQuery)]
internal static partial class DeleteProduct
{
    public static partial Task<int> ExecuteAsync(IVelQuerySession session, int id, CancellationToken cancellationToken = default);
}

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
