using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using VelQuery.MongoDB.Serialization;

namespace VelQuery.MongoDB;

/// <summary>
/// Builds a BSON document (filter or update) from typed arguments. Generated binders construct the document directly
/// from the contract's compile-time-checked JSON; arguments only ever appear as values (ADR-0019).
/// </summary>
public delegate BsonDocument MongoDocumentBinder<TArgs>(in TArgs args);

/// <summary>Builds an aggregation pipeline from typed arguments. A new array is returned on every call.</summary>
public delegate BsonDocument[] MongoPipelineBinder<TArgs>(in TArgs args);

/// <summary>Arguments of a contract that declares no parameters.</summary>
public readonly record struct MongoNoArgs;

/// <summary>
/// Common part of MongoDB operation descriptors. Descriptors are immutable and thread-safe and are cached in static
/// fields by generated code; they hold no BSON documents, only binders that build fresh documents per call, so nothing
/// mutable is shared across operations (AGENTS rule 7).
/// </summary>
public abstract class MongoOperationDescriptor
{
    private protected MongoOperationDescriptor(string queryId, string collectionName, TimeSpan? timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        if (timeout is { } t && t <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive when set.");
        }

        QueryId = queryId;
        CollectionName = collectionName;
        Timeout = timeout;
    }

    /// <summary>Stable, low-cardinality diagnostic identity (VQ-FR-025).</summary>
    public string QueryId { get; }

    /// <summary>The collection the operation targets.</summary>
    public string CollectionName { get; }

    /// <summary>The per-operation time limit, or <see langword="null"/> to use the data source default.</summary>
    public TimeSpan? Timeout { get; }
}

/// <summary>A find contract: filter, optional sort/projection/limit/skip, and the generated document serializer.</summary>
public sealed class MongoFindDescriptor<TArgs, TDocument> : MongoOperationDescriptor
{
    /// <summary>Creates the descriptor.</summary>
    public MongoFindDescriptor(string queryId, string collectionName, MongoDocumentBinder<TArgs> filter, VelQueryBsonSerializer<TDocument> serializer,
        Func<BsonDocument>? sort = null, Func<BsonDocument>? projection = null, int? limit = null, int? skip = null, TimeSpan? timeout = null)
        : base(queryId, collectionName, timeout)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(serializer);
        if (limit is < 0 || skip is < 0)
        {
            throw new ArgumentOutOfRangeException(limit is < 0 ? nameof(limit) : nameof(skip), "Limit and skip must not be negative.");
        }

        Filter = filter;
        Sort = sort;
        Projection = projection;
        Limit = limit;
        Skip = skip;
        Serializer = serializer.ForQuery(queryId);
    }

    /// <summary>Builds the filter.</summary>
    public MongoDocumentBinder<TArgs> Filter { get; }

    /// <summary>Builds the sort document, if any.</summary>
    public Func<BsonDocument>? Sort { get; }

    /// <summary>Builds the projection document, if any.</summary>
    public Func<BsonDocument>? Projection { get; }

    /// <summary>The maximum number of documents, if any.</summary>
    public int? Limit { get; }

    /// <summary>The number of documents to skip, if any.</summary>
    public int? Skip { get; }

    internal IBsonSerializer<TDocument> Serializer { get; }
}

/// <summary>A count contract (<c>countDocuments</c>).</summary>
public sealed class MongoCountDescriptor<TArgs> : MongoOperationDescriptor
{
    /// <summary>Creates the descriptor.</summary>
    public MongoCountDescriptor(string queryId, string collectionName, MongoDocumentBinder<TArgs> filter, long? limit = null, long? skip = null, TimeSpan? timeout = null)
        : base(queryId, collectionName, timeout)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (limit is < 0 || skip is < 0)
        {
            throw new ArgumentOutOfRangeException(limit is < 0 ? nameof(limit) : nameof(skip), "Limit and skip must not be negative.");
        }

        Filter = filter;
        Limit = limit;
        Skip = skip;
    }

    /// <summary>Builds the filter.</summary>
    public MongoDocumentBinder<TArgs> Filter { get; }

    /// <summary>The maximum number of documents to count, if any.</summary>
    public long? Limit { get; }

    /// <summary>The number of documents to skip before counting, if any.</summary>
    public long? Skip { get; }
}

/// <summary>An aggregation contract whose output documents are read with the generated serializer.</summary>
public sealed class MongoAggregateDescriptor<TArgs, TResult> : MongoOperationDescriptor
{
    /// <summary>Creates the descriptor.</summary>
    public MongoAggregateDescriptor(string queryId, string collectionName, MongoPipelineBinder<TArgs> pipeline, VelQueryBsonSerializer<TResult> serializer,
        bool allowDiskUse = false, TimeSpan? timeout = null)
        : base(queryId, collectionName, timeout)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(serializer);
        Pipeline = pipeline;
        AllowDiskUse = allowDiskUse;
        Serializer = serializer.ForQuery(queryId);
    }

    /// <summary>Builds the pipeline stages.</summary>
    public MongoPipelineBinder<TArgs> Pipeline { get; }

    /// <summary>Whether stages may spill to disk.</summary>
    public bool AllowDiskUse { get; }

    internal IBsonSerializer<TResult> Serializer { get; }
}

/// <summary>An update contract (<c>updateOne</c> or <c>updateMany</c>) with an operator update document.</summary>
public sealed class MongoUpdateDescriptor<TArgs> : MongoOperationDescriptor
{
    /// <summary>Creates the descriptor.</summary>
    public MongoUpdateDescriptor(string queryId, string collectionName, MongoDocumentBinder<TArgs> filter, MongoDocumentBinder<TArgs> update,
        bool many = false, bool upsert = false, TimeSpan? timeout = null)
        : base(queryId, collectionName, timeout)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(update);
        Filter = filter;
        Update = update;
        Many = many;
        Upsert = upsert;
    }

    /// <summary>Builds the filter.</summary>
    public MongoDocumentBinder<TArgs> Filter { get; }

    /// <summary>Builds the update document (update operators such as <c>$set</c>).</summary>
    public MongoDocumentBinder<TArgs> Update { get; }

    /// <summary>True for <c>updateMany</c>.</summary>
    public bool Many { get; }

    /// <summary>True to insert a document when none matches.</summary>
    public bool Upsert { get; }
}

/// <summary>A delete contract (<c>deleteOne</c> or <c>deleteMany</c>).</summary>
public sealed class MongoDeleteDescriptor<TArgs> : MongoOperationDescriptor
{
    /// <summary>Creates the descriptor.</summary>
    public MongoDeleteDescriptor(string queryId, string collectionName, MongoDocumentBinder<TArgs> filter, bool many = false, TimeSpan? timeout = null)
        : base(queryId, collectionName, timeout)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Filter = filter;
        Many = many;
    }

    /// <summary>Builds the filter.</summary>
    public MongoDocumentBinder<TArgs> Filter { get; }

    /// <summary>True for <c>deleteMany</c>.</summary>
    public bool Many { get; }
}

/// <summary>An insert contract: one document, or an ordered bulk insert of many.</summary>
public sealed class MongoInsertDescriptor<TDocument> : MongoOperationDescriptor
{
    /// <summary>Creates the descriptor.</summary>
    public MongoInsertDescriptor(string queryId, string collectionName, VelQueryBsonSerializer<TDocument> serializer, TimeSpan? timeout = null)
        : base(queryId, collectionName, timeout)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        Serializer = serializer.ForQuery(queryId);
    }

    internal IBsonSerializer<TDocument> Serializer { get; }
}

/// <summary>
/// Counts reported by the server for an update. VelQuery never computes or estimates them (AGENTS rule 3).
/// </summary>
/// <param name="MatchedCount">Documents matched by the filter.</param>
/// <param name="ModifiedCount">Documents actually changed (a matched document whose values already equal the update is not modified).</param>
/// <param name="UpsertedId">The <c>_id</c> of the inserted document when the update upserted, otherwise <see langword="null"/>.</param>
public readonly record struct VelQueryMongoUpdateResult(long MatchedCount, long ModifiedCount, BsonValue? UpsertedId);

/// <summary>A projection that hands VelQuery's serializer to the driver for one operation (no registry involved).</summary>
internal sealed class SerializerProjection<TDocument>(BsonDocument? projection, IBsonSerializer<TDocument> serializer) : ProjectionDefinition<BsonDocument, TDocument>
{
    public override RenderedProjectionDefinition<TDocument> Render(RenderArgs<BsonDocument> args) => new(projection, serializer);
}
