using Microsoft.AspNetCore.Diagnostics;
using QuickStart.Api.Logging;
using QuickStart.Data.Exceptions;
using VelQuery;

namespace QuickStart.Api.ErrorHandling;

/// <summary>
/// Maps data-layer failures to HTTP problem responses. Details stay in the server log, not the response body.
/// </summary>
internal sealed class DataExceptionHandler(IProblemDetailsService problemDetails, ILogger<DataExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, queryId, category) = exception switch
        {
            DuplicateProductNameException => (StatusCodes.Status409Conflict, "A product with that name already exists.", null, null),
            VelQueryTimeoutException q => (StatusCodes.Status504GatewayTimeout, "The database did not respond in time.", q.QueryId, q.Category.ToString()),
            VelQueryProviderException q => (StatusCodes.Status503ServiceUnavailable, "The database reported an error.", q.QueryId, q.Category.ToString()),
            VelQueryException q => (StatusCodes.Status500InternalServerError, "The query could not be completed.", q.QueryId, q.Category.ToString()),
            _ => (0, null, null, null),
        };

        if (status == 0)
        {
            return false;
        }

        if (status >= StatusCodes.Status500InternalServerError)
        {
            Log.QueryFailed(logger, exception, queryId, category);
        }

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetailsContext { HttpContext = httpContext, Exception = exception };
        problem.ProblemDetails.Status = status;
        problem.ProblemDetails.Title = title;
        if (queryId is not null)
        {
            problem.ProblemDetails.Extensions["queryId"] = queryId;
            problem.ProblemDetails.Extensions["category"] = category;
        }

        return await problemDetails.TryWriteAsync(problem);
    }
}
