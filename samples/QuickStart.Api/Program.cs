using QuickStart.Api;
using QuickStart.Api.Endpoints;
using QuickStart.Data;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Products")
    ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Products'.");

builder.Services.AddProductData(connectionString);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DataExceptionHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

await DatabaseInitializer.EnsureCreatedAsync(connectionString, app.Logger, app.Lifetime.ApplicationStopping);

app.MapGet("/", () => Results.Redirect("/products"));
app.MapProductEndpoints();

app.Run();
