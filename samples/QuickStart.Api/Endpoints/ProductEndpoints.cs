using QuickStart.Api.Data;
using VelQuery;

namespace QuickStart.Api.Endpoints;

public sealed record ProductRequest(string Name, string? Description, decimal Price, int Stock);

internal static class ProductEndpoints
{
    public static RouteGroupBuilder MapProductEndpoints(this IEndpointRouteBuilder routes)
    {
        var products = routes.MapGroup("/products").WithTags("Products");

        products.MapGet("/", async (IVelQuerySession session, CancellationToken ct, int skip = 0, int take = 20) =>
        {
            if (skip < 0 || take is < 1 or > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["paging"] = ["skip must be >= 0 and take between 1 and 100."] });
            }

            return Results.Ok(await ListProducts.ExecuteAsync(session, skip, take, ct));
        });

        products.MapGet("/{id:int}", async (int id, IVelQuerySession session, CancellationToken ct) =>
            await GetProduct.ExecuteAsync(session, id, ct) is { } product ? Results.Ok(product) : Results.NotFound());

        products.MapPost("/", async (ProductRequest request, IVelQuerySession session, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem)
            {
                return problem;
            }

            var id = await CreateProduct.ExecuteAsync(session, request.Name, request.Description, request.Price, request.Stock, ct);
            return Results.Created($"/products/{id}", await GetProduct.ExecuteAsync(session, id, ct));
        });

        products.MapPut("/{id:int}", async (int id, ProductRequest request, IVelQuerySession session, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem)
            {
                return problem;
            }

            var affected = await UpdateProduct.ExecuteAsync(session, id, request.Name, request.Description, request.Price, request.Stock, ct);
            return affected == 0 ? Results.NotFound() : Results.NoContent();
        });

        products.MapDelete("/{id:int}", async (int id, IVelQuerySession session, CancellationToken ct) =>
            await DeleteProduct.ExecuteAsync(session, id, ct) == 0 ? Results.NotFound() : Results.NoContent());

        // Streams rows to the client as a JSON array without buffering the whole table in memory.
        products.MapGet("/export", (IVelQuerySession session, CancellationToken ct) => ExportProducts.ExecuteAsync(session, ct));

        // Runtime SQL path: explicit QueryParameters, still reflection-free because the generator registered a
        // mapper for Product. Values are bound as parameters, never concatenated into the SQL text.
        products.MapGet("/search", async (string q, IVelQuerySession session, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Length > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["q"] = ["Provide 1-100 characters."] });
            }

            var parameters = new QueryParameters()
                .Add("Pattern", "%" + EscapeLike(q) + "%", size: 202);
            var rows = await session
                .Sql("""
                    SELECT TOP (50) Id, Name, Description, Price, Stock, CreatedAt
                    FROM dbo.Products
                    WHERE Name LIKE @Pattern ESCAPE '\'
                    ORDER BY Name
                    """, parameters)
                .WithQueryId("products.search")
                .WithTimeout(TimeSpan.FromSeconds(5))
                .QueryAsync<Product>(ct);
            return Results.Ok(rows);
        });

        return products;
    }

    private static IResult? Validate(ProductRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
        {
            errors["name"] = ["Name is required and must be at most 200 characters."];
        }

        if (request.Description is { Length: > 1000 })
        {
            errors["description"] = ["Description must be at most 1000 characters."];
        }

        if (request.Price < 0 || decimal.Round(request.Price, 2) != request.Price)
        {
            errors["price"] = ["Price must be non-negative with at most 2 decimal places."];
        }

        if (request.Stock < 0)
        {
            errors["stock"] = ["Stock must be non-negative."];
        }

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static string EscapeLike(string value)
        => value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal)
            .Replace("[", @"\[", StringComparison.Ordinal);
}
