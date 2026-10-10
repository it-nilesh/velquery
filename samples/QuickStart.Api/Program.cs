using QuickStart.Api;
using QuickStart.Api.Data;
using QuickStart.Api.Endpoints;
using VelQuery.SqlServer;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Products")
    ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Products'.");

// Registers a singleton VelQueryDataSource (backed by SqlClient's connection pool) and a scoped IVelQuerySession.
builder.Services.AddVelQuerySqlServer(connectionString, options =>
{
    options.DefaultTimeout = TimeSpan.FromSeconds(15);
    options.StrictMapping = true;
    options.AllowDynamicFallback = false;
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<VelQueryExceptionHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

await DatabaseInitializer.EnsureCreatedAsync(connectionString, app.Logger, app.Lifetime.ApplicationStopping);

app.MapGet("/", () => Results.Redirect("/products"));
app.MapProductEndpoints();

app.Run();
