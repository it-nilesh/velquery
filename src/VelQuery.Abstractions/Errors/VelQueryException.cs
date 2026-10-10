using System;

namespace VelQuery;

/// <summary>
/// Base type for failures raised by VelQuery. Messages carry the query identity but never parameter values,
/// row values or connection strings (VQ-SEC-003).
/// </summary>
public class VelQueryException : Exception
{
    /// <summary>Creates an exception with the given category and diagnostic query identity.</summary>
    public VelQueryException(VelQueryErrorCategory category, string? queryId, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
        QueryId = queryId;
    }

    /// <summary>The failure category.</summary>
    public VelQueryErrorCategory Category { get; }

    /// <summary>The stable query identity, when the failure is associated with a query.</summary>
    public string? QueryId { get; }

    internal static string Describe(string? queryId, string message)
        => queryId is null ? message : $"Query '{queryId}': {message}";
}

/// <summary>Raised when an operation requiring at least one row received none.</summary>
public sealed class VelQueryNoRowsException(string? queryId)
    : VelQueryException(VelQueryErrorCategory.NoRows, queryId, Describe(queryId, "the query returned no rows but at least one row was required."));

/// <summary>Raised when a single-row operation received more than one row.</summary>
public sealed class VelQueryTooManyRowsException(string? queryId)
    : VelQueryException(VelQueryErrorCategory.TooManyRows, queryId, Describe(queryId, "the query returned more than one row but exactly one was required."));

/// <summary>Raised when invalid arguments, configuration or API usage are detected.</summary>
public sealed class VelQueryValidationException(string? queryId, string message)
    : VelQueryException(VelQueryErrorCategory.Validation, queryId, Describe(queryId, message));

/// <summary>Raised when a requested capability is not available for the provider or execution mode.</summary>
public sealed class VelQueryUnsupportedCapabilityException(string? queryId, string message)
    : VelQueryException(VelQueryErrorCategory.UnsupportedCapability, queryId, Describe(queryId, message));

/// <summary>Raised when the provider reports a command timeout. The provider exception is the inner exception.</summary>
public sealed class VelQueryTimeoutException(string? queryId, Exception innerException)
    : VelQueryException(VelQueryErrorCategory.Timeout, queryId, Describe(queryId, "the command timed out."), innerException);

/// <summary>Raised when a transaction cannot be started, completed or used.</summary>
public sealed class VelQueryTransactionException(string? queryId, string message, Exception? innerException = null)
    : VelQueryException(VelQueryErrorCategory.Transaction, queryId, Describe(queryId, message), innerException);

/// <summary>
/// Raised when the database provider fails. The original provider exception is preserved as
/// <see cref="Exception.InnerException"/>; its message is not copied because provider messages can echo data.
/// </summary>
public sealed class VelQueryProviderException(string? queryId, Exception innerException)
    : VelQueryException(VelQueryErrorCategory.Provider, queryId,
        Describe(queryId, $"the database provider reported an error ({innerException.GetType().Name}). See InnerException for details."),
        innerException);
