using QuickStart.Data.Exceptions;
using QuickStart.Data.Models;

namespace QuickStart.Data.Repositories;

/// <summary>Product data access. Inject this (not the generated queries) so callers can be unit-tested with a fake.</summary>
public interface IProductRepository
{
    /// <summary>Returns a page of products ordered by id.</summary>
    Task<IReadOnlyList<Product>> ListAsync(int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Returns the product, or <see langword="null"/> if it does not exist.</summary>
    Task<Product?> GetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Creates a product. Throws <see cref="DuplicateProductNameException"/> if the name is taken.</summary>
    Task<Product> CreateAsync(ProductInput input, CancellationToken cancellationToken = default);

    /// <summary>Updates a product; returns false if it does not exist. Throws <see cref="DuplicateProductNameException"/> if the name is taken.</summary>
    Task<bool> UpdateAsync(int id, ProductInput input, CancellationToken cancellationToken = default);

    /// <summary>Deletes a product; returns false if it does not exist.</summary>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Returns up to 50 products whose name contains <paramref name="text"/> (treated literally, not as a pattern).</summary>
    Task<IReadOnlyList<Product>> SearchAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Streams every product ordered by id.</summary>
    IAsyncEnumerable<Product> ExportAsync(CancellationToken cancellationToken = default);
}
