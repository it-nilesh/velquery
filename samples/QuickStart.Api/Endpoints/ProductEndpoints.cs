using QuickStart.Data;

namespace QuickStart.Api.Endpoints;

internal static class ProductEndpoints
{
    public static RouteGroupBuilder MapProductEndpoints(this IEndpointRouteBuilder routes)
    {
        var products = routes.MapGroup("/products").WithTags("Products");

        products.MapGet("/", async (IProductRepository repository, CancellationToken ct, int skip = 0, int take = 20) =>
            skip < 0 || take is < 1 or > 100
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["paging"] = ["skip must be >= 0 and take between 1 and 100."] })
                : Results.Ok(await repository.ListAsync(skip, take, ct)));

        products.MapGet("/{id:int}", async (int id, IProductRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, ct) is { } product ? Results.Ok(product) : Results.NotFound());

        products.MapPost("/", async (ProductInput input, IProductRepository repository, CancellationToken ct) =>
        {
            if (input.Validate() is { Count: > 0 } errors)
            {
                return Results.ValidationProblem(errors);
            }

            var product = await repository.CreateAsync(input, ct);
            return Results.Created($"/products/{product.Id}", product);
        });

        products.MapPut("/{id:int}", async (int id, ProductInput input, IProductRepository repository, CancellationToken ct) =>
        {
            if (input.Validate() is { Count: > 0 } errors)
            {
                return Results.ValidationProblem(errors);
            }

            return await repository.UpdateAsync(id, input, ct) ? Results.NoContent() : Results.NotFound();
        });

        products.MapDelete("/{id:int}", async (int id, IProductRepository repository, CancellationToken ct) =>
            await repository.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        products.MapGet("/search", async (string q, IProductRepository repository, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q) || q.Length > 100
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["q"] = ["Provide 1-100 characters."] })
                : Results.Ok(await repository.SearchAsync(q, ct)));

        // Streams rows to the client as a JSON array without buffering the whole table in memory.
        products.MapGet("/export", (IProductRepository repository, CancellationToken ct) => repository.ExportAsync(ct));

        return products;
    }
}
