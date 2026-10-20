using System;

namespace VelQuery.MongoDB;

/// <summary>
/// Base for MongoDB query contracts (ADR-0019). Apply a derived attribute to a <see langword="partial"/> class that
/// declares exactly one <see langword="partial"/> method without a body; VelQuery.MongoDB.Generators checks the JSON at
/// build time and emits a cached descriptor, a typed argument binder that builds the BSON directly, a reflection-free
/// serializer for the document type and the method body.
/// </summary>
/// <remarks>
/// <para><b>Method shape.</b> The first parameter is a <see cref="VelQueryMongoSession"/>; an optional
/// <see cref="System.Threading.CancellationToken"/> may be last. Every other parameter binds to the placeholder
/// <c>@name</c> with the same name (case-insensitive).</para>
/// <para><b>JSON.</b> Filters, updates, sorts, projections and pipelines are strict JSON (quoted keys) with MongoDB
/// Extended JSON v2 wrappers (<c>$oid</c>, <c>$date</c>, <c>$numberLong</c>, <c>$numberDecimal</c>, <c>$numberInt</c>,
/// <c>$numberDouble</c>, <c>$uuid</c>, <c>$binary</c>, <c>$regularExpression</c>, <c>$timestamp</c>, <c>$minKey</c>,
/// <c>$maxKey</c>) and <c>@name</c> placeholders. A placeholder may appear only where a <em>value</em> is expected, never
/// as a key or operator, and its parameter must be a scalar (or a sequence of scalars), so an argument can never turn
/// into an operator such as <c>$where</c>. Inside aggregation expressions arguments are wrapped in <c>$literal</c>, so a
/// string such as <c>"$secret"</c> cannot become a field path. Placeholders are rejected where values are code or
/// identifiers (<c>$where</c>, <c>$function</c>, <c>$accumulator</c>, <c>$rename</c>, <c>$lookup</c>, sort and
/// projection documents, …). <c>"@name"</c> inside quotes is an ordinary string.</para>
/// </remarks>
public abstract class VelQueryMongoContractAttribute : Attribute
{
    private protected VelQueryMongoContractAttribute(string queryId, string collection)
    {
        QueryId = queryId;
        Collection = collection;
    }

    /// <summary>Stable, low-cardinality diagnostic identity (VQ-FR-025). Must not contain values.</summary>
    public string QueryId { get; }

    /// <summary>The collection name.</summary>
    public string Collection { get; }

    /// <summary>Per-operation time limit in milliseconds; <c>0</c> (the default) uses the data source default.</summary>
    public int TimeoutMilliseconds { get; set; }
}

/// <summary>
/// A find contract. The return type selects the operation: <c>Task&lt;IReadOnlyList&lt;T&gt;&gt;</c> (buffered),
/// <c>IAsyncEnumerable&lt;T&gt;</c> (streamed), <c>Task&lt;T&gt;</c> (single, or first with
/// <see cref="Cardinality"/>), <c>Task&lt;T?&gt;</c> (single-or-default, or first-or-default).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class VelQueryMongoFindAttribute(string queryId, string collection, string filter) : VelQueryMongoContractAttribute(queryId, collection)
{
    /// <summary>The filter document (JSON with <c>@name</c> placeholders).</summary>
    public string Filter { get; } = filter;

    /// <summary>A constant sort document, for example <c>{ "name": 1 }</c>.</summary>
    public string? Sort { get; set; }

    /// <summary>A constant projection document. Fields the projection removes must be nullable on the document type.</summary>
    public string? Projection { get; set; }

    /// <summary>Maximum number of documents; <c>0</c> (the default) means no limit.</summary>
    public int Limit { get; set; }

    /// <summary>Number of documents to skip.</summary>
    public int Skip { get; set; }

    /// <summary>Overrides the operation inferred from the return type (Many, First, FirstOrDefault, Single, SingleOrDefault).</summary>
    public QueryCardinality Cardinality { get; set; }
}

/// <summary>A count contract (<c>countDocuments</c>); the method returns <c>Task&lt;long&gt;</c>.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class VelQueryMongoCountAttribute(string queryId, string collection, string filter) : VelQueryMongoContractAttribute(queryId, collection)
{
    /// <summary>The filter document.</summary>
    public string Filter { get; } = filter;

    /// <summary>Maximum number of documents to count; <c>0</c> means no limit.</summary>
    public int Limit { get; set; }

    /// <summary>Number of documents to skip before counting.</summary>
    public int Skip { get; set; }
}

/// <summary>
/// An aggregation contract. <paramref name="pipeline"/> is a JSON array of stages. Return types are those of
/// <see cref="VelQueryMongoFindAttribute"/>. Write stages (<c>$out</c>, <c>$merge</c>) are rejected.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class VelQueryMongoAggregateAttribute(string queryId, string collection, string pipeline) : VelQueryMongoContractAttribute(queryId, collection)
{
    /// <summary>The pipeline (a JSON array of stage documents).</summary>
    public string Pipeline { get; } = pipeline;

    /// <summary>Whether stages may write temporary data to disk.</summary>
    public bool AllowDiskUse { get; set; }

    /// <summary>Overrides the operation inferred from the return type.</summary>
    public QueryCardinality Cardinality { get; set; }
}

/// <summary>
/// An update contract; the method returns <c>Task&lt;VelQueryMongoUpdateResult&gt;</c> with the server's matched and
/// modified counts. <paramref name="update"/> must contain only update operators (<c>$set</c>, <c>$inc</c>, …).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class VelQueryMongoUpdateAttribute(string queryId, string collection, string filter, string update) : VelQueryMongoContractAttribute(queryId, collection)
{
    /// <summary>The filter document.</summary>
    public string Filter { get; } = filter;

    /// <summary>The update document.</summary>
    public string Update { get; } = update;

    /// <summary>True for <c>updateMany</c>; the default is <c>updateOne</c>.</summary>
    public bool Many { get; set; }

    /// <summary>True to insert a document when none matches.</summary>
    public bool Upsert { get; set; }
}

/// <summary>A delete contract; the method returns <c>Task&lt;long&gt;</c>, the deleted count the server reports.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class VelQueryMongoDeleteAttribute(string queryId, string collection, string filter) : VelQueryMongoContractAttribute(queryId, collection)
{
    /// <summary>The filter document.</summary>
    public string Filter { get; } = filter;

    /// <summary>True for <c>deleteMany</c>; the default is <c>deleteOne</c>.</summary>
    public bool Many { get; set; }
}

/// <summary>
/// An insert contract. A method taking one document returns <c>Task</c> (insert one); a method taking an
/// <c>IEnumerable&lt;T&gt;</c> returns <c>Task&lt;long&gt;</c> (ordered bulk insert; see
/// <see cref="VelQueryMongoSession.InsertManyAsync{TDocument}"/> for partial-failure semantics).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class VelQueryMongoInsertAttribute(string queryId, string collection) : VelQueryMongoContractAttribute(queryId, collection);
