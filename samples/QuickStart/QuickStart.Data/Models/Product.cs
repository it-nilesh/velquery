namespace QuickStart.Data.Models;

/// <summary>A row of <c>dbo.Products</c>. The generator emits a reflection-free mapper for it at build time.</summary>
public sealed record Product(int Id, string Name, string? Description, decimal Price, int Stock, DateTimeOffset CreatedAt);
