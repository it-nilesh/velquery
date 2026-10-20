using System;
using MongoDB.Bson;
using MongoDB.Driver;
using VelQuery.Engine;
using VelQuery.Providers;

namespace VelQuery.MongoDB;

/// <summary>
/// Applies VelQuery's error contract (VQ-FR-026) to MongoDB.Driver failures (ADR-0016). Exceptions from the driver are
/// wrapped in <see cref="VelQueryProviderException"/> with the original as the inner exception, so server error codes
/// (for example duplicate key 11000 on <see cref="MongoWriteCommandException"/>) and
/// error labels stay inspectable while VelQuery's own message carries no document or parameter values.
/// </summary>
public sealed class MongoErrorClassifier : IVelQueryErrorClassifier
{
    // Server error codes for operations stopped by a time limit (MaxTimeMSExpired, ExceededTimeLimit).
    private const int MaxTimeMSExpired = 50;
    private const int ExceededTimeLimit = 262;

    private MongoErrorClassifier()
    {
    }

    /// <summary>The shared classifier.</summary>
    public static MongoErrorClassifier Instance { get; } = new();

    /// <inheritdoc />
    public ProviderErrorKind Classify(Exception exception) => exception switch
    {
        // maxTimeMS on find/count/getMore surfaces as MongoExecutionTimeoutException; some commands report the same
        // server code through a plain MongoCommandException.
        MongoExecutionTimeoutException => ProviderErrorKind.Timeout,
        MongoCommandException { Code: MaxTimeMSExpired or ExceededTimeLimit } => ProviderErrorKind.Timeout,
        MongoWriteCommandException { Code: MaxTimeMSExpired or ExceededTimeLimit } => ProviderErrorKind.Timeout,

        // Server selection and connection-pool wait timeouts are reported by the driver as System.TimeoutException.
        TimeoutException => ProviderErrorKind.Timeout,
        OperationCanceledException => ProviderErrorKind.Cancelled,
        _ => ProviderErrorKind.Other,
    };

    /// <inheritdoc />
    public bool IsProviderException(Exception exception) => exception is MongoException or BsonException;
}
