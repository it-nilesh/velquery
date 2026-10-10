namespace QuickStart.Data;

/// <summary>A row of <c>dbo.Products</c>. The generator emits a reflection-free mapper for it at build time.</summary>
public sealed record Product(int Id, string Name, string? Description, decimal Price, int Stock, DateTimeOffset CreatedAt);

/// <summary>Values supplied when creating or updating a product.</summary>
public sealed record ProductInput(string Name, string? Description, decimal Price, int Stock)
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 1000;

    /// <summary>Returns validation errors keyed by field; empty when the input is valid.</summary>
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > MaxNameLength)
        {
            errors["name"] = [$"Name is required and must be at most {MaxNameLength} characters."];
        }

        if (Description is { Length: > MaxDescriptionLength })
        {
            errors["description"] = [$"Description must be at most {MaxDescriptionLength} characters."];
        }

        if (Price < 0 || decimal.Round(Price, 2) != Price)
        {
            errors["price"] = ["Price must be non-negative with at most 2 decimal places."];
        }

        if (Stock < 0)
        {
            errors["stock"] = ["Stock must be non-negative."];
        }

        return errors;
    }
}

/// <summary>Raised when a product name collides with an existing product (unique constraint).</summary>
public sealed class DuplicateProductNameException(string name, Exception innerException)
    : Exception($"A product named '{name}' already exists.", innerException)
{
    public string Name { get; } = name;
}
