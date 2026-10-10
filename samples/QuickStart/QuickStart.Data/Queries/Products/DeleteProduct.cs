using VelQuery;

namespace QuickStart.Data.Queries.Products;

[VelQuerySql("products.delete", "DELETE FROM dbo.Products WHERE Id = @Id", Cardinality = QueryCardinality.NonQuery)]
internal static partial class DeleteProduct
{
    public static partial Task<int> ExecuteAsync(IVelQuerySession session, int id, CancellationToken cancellationToken = default);
}
