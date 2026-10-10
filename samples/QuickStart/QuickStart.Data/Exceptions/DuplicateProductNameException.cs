namespace QuickStart.Data.Exceptions;

/// <summary>Raised when a product name collides with an existing product (unique constraint).</summary>
public sealed class DuplicateProductNameException(string name, Exception innerException)
    : Exception($"A product named '{name}' already exists.", innerException)
{
    public string Name { get; } = name;
}
