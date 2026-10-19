using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using VelQuery.Providers;

namespace VelQuery.Engine;

/// <summary>
/// The engine-neutral error contract (VQ-FR-026), shared by every engine so translation cannot drift:
/// <list type="number">
/// <item>VelQuery exceptions and caller-requested cancellation pass through with their original stack.</item>
/// <item>Any failure after the caller's token was cancelled becomes <see cref="OperationCanceledException"/>.</item>
/// <item>Driver timeouts become <see cref="VelQueryTimeoutException"/>; driver cancellation becomes
/// <see cref="OperationCanceledException"/>; other driver failures become <see cref="VelQueryProviderException"/>.
/// The driver exception is always preserved as the inner exception.</item>
/// <item>Everything else (for example exceptions thrown by caller code) passes through unchanged.</item>
/// </list>
/// Messages carry the query identity only, never values.
/// </summary>
public static class VelQueryErrors
{
    /// <summary>
    /// Returns the exception to throw for <paramref name="exception"/>. Pass-through cases are rethrown here with their
    /// original stack trace, so callers write <c>throw VelQueryErrors.Translate(...)</c>.
    /// </summary>
    public static Exception Translate(Exception exception, string queryId, IVelQueryErrorClassifier classifier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(classifier);
        switch (exception)
        {
            case VelQueryException:
                return Rethrow(exception);
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                return Rethrow(exception);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException($"Query '{queryId}' was cancelled.", exception, cancellationToken);
        }

        return classifier.Classify(exception) switch
        {
            ProviderErrorKind.Timeout => new VelQueryTimeoutException(queryId, exception),
            ProviderErrorKind.Cancelled => new OperationCanceledException($"Query '{queryId}' was cancelled by the provider.", exception),
            _ when classifier.IsProviderException(exception) => new VelQueryProviderException(queryId, exception),
            _ => Rethrow(exception),
        };
    }

    private static Exception Rethrow(Exception exception)
    {
        ExceptionDispatchInfo.Capture(exception).Throw();
        return exception;
    }
}
