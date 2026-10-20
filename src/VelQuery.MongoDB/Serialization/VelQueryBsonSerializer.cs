using System;
using System.ComponentModel;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace VelQuery.MongoDB.Serialization;

/// <summary>
/// Base for the BSON serializers VelQuery.MongoDB.Generators emits per document type (ADR-0017). Generated serializers
/// read fields by name with typed <see cref="IBsonReader"/> calls, apply VelQuery's strict mapping rules (VQ-FR-016)
/// and never use reflection, class maps or the driver's global serializer registry. They reach the driver per
/// operation, through descriptors, so no process-wide registration is needed or made.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public abstract class VelQueryBsonSerializer<T> : IBsonSerializer<T>
{
    /// <inheritdoc />
    public Type ValueType => typeof(T);

    /// <summary>
    /// Reads a document. The reader is positioned on a BSON document value (its type has been read). Mapping failures
    /// throw <see cref="VelQueryMappingException"/> naming <paramref name="path"/> plus the field, never the value.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <param name="queryId">The query identity for errors, if known.</param>
    /// <param name="path">The dotted path of this document inside its parent, or <see langword="null"/> at the root.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public abstract T Read(IBsonReader reader, string? queryId, string? path);

    /// <summary>Writes <paramref name="value"/> as a BSON document.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The document.</param>
    /// <param name="queryId">The query identity for errors, if known.</param>
    /// <param name="path">The dotted path of this document inside its parent, or <see langword="null"/> at the root.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public abstract void Write(IBsonWriter writer, T value, string? queryId, string? path);

    /// <inheritdoc />
    public T Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Read(context.Reader, null, null);
    }

    /// <inheritdoc />
    public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, T value)
    {
        ArgumentNullException.ThrowIfNull(context);
        Write(context.Writer, value, null, null);
    }

    /// <inheritdoc />
    void IBsonSerializer.Serialize(BsonSerializationContext context, BsonSerializationArgs args, object value)
        => Serialize(context, args, (T)value);

    /// <inheritdoc />
    object IBsonSerializer.Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => Deserialize(context, args)!;

    /// <summary>A view of this serializer that reports <paramref name="queryId"/> in mapping errors.</summary>
    internal IBsonSerializer<T> ForQuery(string queryId) => new QueryBound(this, queryId);

    private sealed class QueryBound(VelQueryBsonSerializer<T> inner, string queryId) : IBsonSerializer<T>
    {
        public Type ValueType => typeof(T);

        public T Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) => inner.Read(context.Reader, queryId, null);

        public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, T value) => inner.Write(context.Writer, value, queryId, null);

        void IBsonSerializer.Serialize(BsonSerializationContext context, BsonSerializationArgs args, object value) => Serialize(context, args, (T)value);

        object IBsonSerializer.Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) => Deserialize(context, args)!;
    }
}
