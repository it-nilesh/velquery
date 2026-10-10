using Microsoft.Data.SqlClient;
using QuickStart.Data.Exceptions;
using QuickStart.Data.Models;
using QuickStart.Data.Queries.Products;
using VelQuery;

namespace QuickStart.Data.Repositories;

/// <summary>
/// SQL Server implementation over one scoped <see cref="IVelQuerySession"/>. Translates database-specific failures
/// into domain exceptions so callers never depend on SqlClient.
/// </summary>
internal sealed class ProductRepository(IVelQuerySession session) : IProductRepository
{
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    public Task<IReadOnlyList<Product>> ListAsync(int skip, int take, CancellationToken cancellationToken = default)
        => ListProducts.ExecuteAsync(session, skip, take, cancellationToken);

    public Task<Product?> GetAsync(int id, CancellationToken cancellationToken = default)
        => GetProduct.ExecuteAsync(session, id, cancellationToken);

    public async Task<Product> CreateAsync(ProductInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            return await CreateProduct.ExecuteAsync(session, input.Name, input.Description, input.Price, input.Stock, cancellationToken);
        }
        catch (VelQueryProviderException ex) when (IsUniqueViolation(ex))
        {
            throw new DuplicateProductNameException(input.Name, ex);
        }
    }

    public async Task<bool> UpdateAsync(int id, ProductInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            return await UpdateProduct.ExecuteAsync(session, id, input.Name, input.Description, input.Price, input.Stock, cancellationToken) > 0;
        }
        catch (VelQueryProviderException ex) when (IsUniqueViolation(ex))
        {
            throw new DuplicateProductNameException(input.Name, ex);
        }
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
        => await DeleteProduct.ExecuteAsync(session, id, cancellationToken) > 0;

    // Runtime SQL with explicit QueryParameters: still reflection-free because the generator registered a mapper for
    // Product. The search text is bound as a parameter and LIKE wildcards are escaped, so it is matched literally.
    public Task<IReadOnlyList<Product>> SearchAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var parameters = new QueryParameters()
            .Add("Pattern", "%" + EscapeLike(text) + "%", size: 2 * ProductInput.MaxNameLength + 2);
        return session
            .Sql("""
                SELECT TOP (50) Id, Name, Description, Price, Stock, CreatedAt
                FROM dbo.Products
                WHERE Name LIKE @Pattern ESCAPE '\'
                ORDER BY Name
                """, parameters)
            .WithQueryId("products.search")
            .WithTimeout(TimeSpan.FromSeconds(5))
            .QueryAsync<Product>(cancellationToken);
    }

    public IAsyncEnumerable<Product> ExportAsync(CancellationToken cancellationToken = default)
        => ExportProducts.ExecuteAsync(session, cancellationToken);

    private static bool IsUniqueViolation(VelQueryProviderException exception)
        => exception.InnerException is SqlException { Number: UniqueConstraintViolation or UniqueIndexViolation };

    private static string EscapeLike(string value)
        => value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal)
            .Replace("[", @"\[", StringComparison.Ordinal);
}
