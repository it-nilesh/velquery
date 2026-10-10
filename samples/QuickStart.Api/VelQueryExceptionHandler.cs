using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using VelQuery;

namespace QuickStart.Api;

/// <summary>
/// Maps VelQuery's error categories to HTTP problem responses. VelQuery messages never contain parameter or row
/// values, but provider details stay in the server log rather than the response.
/// </summary>
internal sealed class VelQueryExceptionHandler(IProblemDetailsService problemDetails, ILogger<VelQueryExceptionHandler> logger) : IExceptionHandler
{
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not VelQueryException velQuery)
        {
            return false;
        }

        var (status, title) = velQuery switch
        {
            VelQueryProviderException { InnerException: SqlException { Number: UniqueConstraintViolation or UniqueIndexViolation } }
                => (StatusCodes.Status409Conflict, "A product with that name already exists."),
            VelQueryTimeoutException => (StatusCodes.Status504GatewayTimeout, "The database did not respond in time."),
            VelQueryProviderException => (StatusCodes.Status503ServiceUnavailable, "The database reported an error."),
            _ => (StatusCodes.Status500InternalServerError, "The query could not be completed."),
        };

        Log.QueryFailed(logger, exception, velQuery.QueryId, velQuery.Category);
        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                Extensions = { ["queryId"] = velQuery.QueryId, ["category"] = velQuery.Category.ToString() },
            },
        });
    }
}
