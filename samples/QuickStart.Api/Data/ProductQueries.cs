using System.Data;
using VelQuery;

namespace QuickStart.Api.Data;

/// <summary>A row of <c>dbo.Products</c>. The generator emits a reflection-free mapper for it at build time.</summary>
public sealed record Product(int Id, string Name, string? Description, decimal Price, int Stock, DateTimeOffset CreatedAt);

// Each contract below is a static SQL statement checked at build time: a misspelled @parameter is error VQ1001 and an
// unused method parameter is warning VQ1002. The generator implements every partial method.

[VelQuerySql("products.list", """
    SELECT Id, Name, Description, Price, Stock, CreatedAt
    FROM dbo.Products
    ORDER BY Id
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY
    """)]
public static partial class ListProducts
{
    public static partial Task<IReadOnlyList<Product>> ExecuteAsync(IVelQuerySession session, int skip, int take, CancellationToken cancellationToken = default);
}

// Task<Product?> with the default cardinality means SingleOrDefault: null for no row, an error for more than one.
[VelQuerySql("products.by-id", """
    SELECT Id, Name, Description, Price, Stock, CreatedAt
    FROM dbo.Products
    WHERE Id = @Id
    """)]
public static partial class GetProduct
{
    public static partial Task<Product?> ExecuteAsync(IVelQuerySession session, int id, CancellationToken cancellationToken = default);
}

[VelQuerySql("products.create", """
    INSERT INTO dbo.Products (Name, Description, Price, Stock)
    OUTPUT INSERTED.Id
    VALUES (@Name, @Description, @Price, @Stock)
    """, Cardinality = QueryCardinality.Scalar)]
public static partial class CreateProduct
{
    public static partial Task<int> ExecuteAsync(
        IVelQuerySession session,
        [VelQueryParameter(DbType = DbType.String, Size = 200)] string name,
        [VelQueryParameter(DbType = DbType.String, Size = 1000)] string? description,
        [VelQueryParameter(Precision = 18, Scale = 2)] decimal price,
        int stock,
        CancellationToken cancellationToken = default);
}

[VelQuerySql("products.update", """
    UPDATE dbo.Products
    SET Name = @Name, Description = @Description, Price = @Price, Stock = @Stock
    WHERE Id = @Id
    """, Cardinality = QueryCardinality.NonQuery)]
public static partial class UpdateProduct
{
    public static partial Task<int> ExecuteAsync(
        IVelQuerySession session,
        int id,
        [VelQueryParameter(DbType = DbType.String, Size = 200)] string name,
        [VelQueryParameter(DbType = DbType.String, Size = 1000)] string? description,
        [VelQueryParameter(Precision = 18, Scale = 2)] decimal price,
        int stock,
        CancellationToken cancellationToken = default);
}

[VelQuerySql("products.delete", "DELETE FROM dbo.Products WHERE Id = @Id", Cardinality = QueryCardinality.NonQuery)]
public static partial class DeleteProduct
{
    public static partial Task<int> ExecuteAsync(IVelQuerySession session, int id, CancellationToken cancellationToken = default);
}

// IAsyncEnumerable<T> streams rows one at a time; the reader and connection are released when enumeration ends,
// including when the HTTP client disconnects mid-response.
[VelQuerySql("products.export", """
    SELECT Id, Name, Description, Price, Stock, CreatedAt
    FROM dbo.Products
    ORDER BY Id
    """)]
public static partial class ExportProducts
{
    public static partial IAsyncEnumerable<Product> ExecuteAsync(IVelQuerySession session, CancellationToken cancellationToken = default);
}
